using System;
using System.Collections.Generic;
using System.IO;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

static class Patcher
{
    static ModuleDefMD mod;

    static int Main(string[] args)
    {
        if (args.Length == 1 && (args[0] == "--help" || args[0] == "-h"))
        {
            Console.WriteLine("Usage: patch.exe <original.exe> <new-output.exe>");
            return 0;
        }
        try
        {
            Run(args);
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("ERROR: " + e.Message);
            return 1;
        }
        finally
        {
            if (mod != null) mod.Dispose();
        }
    }

    static void Run(string[] args)
    {
        if (args.Length != 2)
            throw new ArgumentException("Usage: patch.exe <original.exe> <new-output.exe>");
        string src = Path.GetFullPath(args[0]);
        string dst = Path.GetFullPath(args[1]);
        if (String.Equals(src, dst, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Input and output must be different files.");
        if (!File.Exists(src)) throw new FileNotFoundException("Input file not found.", src);
        if (File.Exists(dst) || Directory.Exists(dst))
            throw new IOException("Output already exists; choose a new output path.");
        if (!Directory.Exists(Path.GetDirectoryName(dst)))
            throw new DirectoryNotFoundException("Output directory does not exist.");
        mod = ModuleDefMD.Load(src);

        // 1) ModSecret.RestrictMcDownload = False
        TypeDef modSecret = RequireType("PCL.ModSecret");
        FieldDef restrict = modSecret.FindField("RestrictMcDownload");
        if (restrict == null || !restrict.IsLiteral || restrict.FieldType.ElementType != ElementType.Boolean ||
            restrict.Constant == null || !(restrict.Constant.Value is bool) || !(bool)restrict.Constant.Value)
            throw new InvalidOperationException("Unsupported or already patched input: RestrictMcDownload must be a Boolean constant set to True.");
        restrict.Constant.Value = false;
        Console.WriteLine("[1] RestrictMcDownload -> " + restrict.Constant.Value);

        PatchLoaderInit();
        PatchVisibility();
        PatchModpackInstall();
        PatchModpackInstallNoArg();

        // Write beside the destination, then rename without replacing any existing file.
        // Failed patching/writing must never leave a partial destination or truncate the input.
        string temp = Path.Combine(Path.GetDirectoryName(dst), ".pcl-patch-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (FileStream output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                mod.Write(output);
            File.Move(temp, dst);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
        Console.WriteLine("[OK] written: " + dst);
    }

    // 2) PageDownloadInstall.LoaderInit: replace the "restricted" throwing loader with the real DlClientListLoader
    static void PatchLoaderInit()
    {
        TypeDef t = RequireType("PCL.PageDownloadInstall");
        MethodDef m = FindMethod(t, "LoaderInit");
        if (!m.HasBody) throw new InvalidOperationException("LoaderInit has no IL body.");
        IList<Instruction> ins = m.Body.Instructions;

        int k = -1;
        for (int i = 0; i + 12 < ins.Count; i++)
        {
            if (ins[i].OpCode.Code != Code.Ldstr) continue;
            if (ins[i + 12].OpCode.Code != Code.Newobj) continue;
            IMethod ctor = ins[i + 12].Operand as IMethod;
            if (ctor == null || ctor.DeclaringType == null) continue;
            if (!ctor.DeclaringType.Name.String.Contains("LoaderTask")) continue;
            if (k >= 0) throw new InvalidOperationException("Ambiguous LoaderInit pattern.");
            k = i;
        }
        if (k < 0) throw new Exception("LoaderInit pattern not found");

        FieldDef dl = RequireType("PCL.ModDownload").FindField("DlClientListLoader");
        IMethod loaderCtor = (IMethod)ins[k + 12].Operand;
        if (dl == null || !dl.IsStatic ||
            !new SigComparer().Equals(dl.FieldType, loaderCtor.DeclaringType.ToTypeSig()))
            throw new InvalidOperationException("DlClientListLoader type does not match the loader constructor.");
        RejectEntryIntoRange(m.Body, k + 1, 12);
        ins[k].OpCode = OpCodes.Ldsfld;
        ins[k].Operand = dl;
        for (int j = 1; j <= 12; j++)
        {
            ins[k + j].OpCode = OpCodes.Nop;
            ins[k + j].Operand = null;
        }
        Console.WriteLine("[2] LoaderInit -> DlClientListLoader");
    }

    // 3) PageDownloadModify.LoadInstanceFinished: CardMinecraft.Visibility = Visible
    static void PatchVisibility()
    {
        TypeDef t = RequireType("PCL.PageDownloadModify");
        int n = 0;
        foreach (MethodDef m in t.Methods)
        {
            if (!m.HasBody) continue;
            IList<Instruction> ins = m.Body.Instructions;
            for (int i = 0; i + 2 < ins.Count; i++)
            {
                IMethod get = ins[i].Operand as IMethod;
                if (get == null || get.Name.String != "get_CardMinecraft" ||
                    get.DeclaringType.FullName != t.FullName || get.MethodSig.Params.Count != 0 ||
                    (ins[i].OpCode.Code != Code.Call && ins[i].OpCode.Code != Code.Callvirt)) continue;
                if (ins[i + 1].OpCode.Code != Code.Ldc_I4_2) continue;
                IMethod set = ins[i + 2].Operand as IMethod;
                if (set == null || set.Name.String != "set_Visibility" || set.MethodSig.Params.Count != 1 ||
                    set.MethodSig.Params[0].FullName != "System.Windows.Visibility" ||
                    (ins[i + 2].OpCode.Code != Code.Call && ins[i + 2].OpCode.Code != Code.Callvirt)) continue;
                RejectEntryIntoRange(m.Body, i + 1, 2);
                ins[i + 1].OpCode = OpCodes.Ldc_I4_0;
                ins[i + 1].Operand = null;
                n++;
            }
        }
        if (n == 0) throw new Exception("CardMinecraft visibility pattern not found");
        Console.WriteLine("[3] CardMinecraft visible sites: " + n);
    }

    // 4) ModpackInstall(File, InstanceName, Logo): restore the pack-type dispatch that was removed by const folding
    static void PatchModpackInstall()
    {
        TypeDef t = RequireType("PCL.ModModpack");
        MethodDef target = FindMethod(t, "ModpackInstall", 3);
        RequireInstallSignature(target, false);
        if (!target.HasBody || target.Body.Variables.Count < 5)
            throw new InvalidOperationException("Unsupported ModpackInstall body.");
        if (target.Body.Variables[0].Type.ElementType != ElementType.Boolean ||
            target.Body.Variables[2].Type.ElementType != ElementType.String ||
            target.Body.Variables[4].Type.ElementType != ElementType.I4)
            throw new InvalidOperationException("Unsupported ModpackInstall local variable layout.");

        CilBody body = target.Body;
        body.SimplifyBranches();
        IList<Instruction> ins = body.Instructions;

        int ti = -1;
        for (int i = 0; i < ins.Count; i++)
        {
            if (ins[i].OpCode.Code != Code.Throw) continue;
            if (i < 2) continue;
            if (ins[i - 1].OpCode.Code != Code.Newobj) continue;
            if (ins[i - 2].OpCode.Code != Code.Ldstr) continue;
            string s = ins[i - 2].Operand as string;
            if (s == null || !s.Contains("qwq")) continue;
            if (ti >= 0) throw new InvalidOperationException("Ambiguous restriction throw pattern.");
            ti = i;
        }
        if (ti < 0) throw new Exception("restriction throw not found");

        Instruction ldstrIns = ins[ti - 2];
        RejectEntryIntoRange(body, ti - 1, 2);

        Local v0 = body.Variables[0];
        Local v1 = body.Variables[1];
        Local v2 = body.Variables[2];
        Local v4 = body.Variables[4];

        MethodDef dispatch = CreateDispatch(t, target);
        Instruction lout = Instruction.Create(OpCodes.Ldloc, v0);

        ldstrIns.OpCode = OpCodes.Ldloc;
        ldstrIns.Operand = v4;

        List<Instruction> seq = new List<Instruction>();
        seq.Add(Instruction.Create(OpCodes.Ldarg_0));
        seq.Add(Instruction.Create(OpCodes.Ldloc, v1));
        seq.Add(Instruction.Create(OpCodes.Ldloc, v2));
        seq.Add(Instruction.Create(OpCodes.Ldarg_1));
        seq.Add(Instruction.Create(OpCodes.Ldarg_2));
        seq.Add(Instruction.Create(OpCodes.Call, dispatch));
        seq.Add(Instruction.Create(OpCodes.Stloc, v0));
        seq.Add(Instruction.Create(OpCodes.Leave, lout));

        int start = ins.IndexOf(ldstrIns);
        ins.RemoveAt(start + 2);
        ins.RemoveAt(start + 1);
        for (int j = 0; j < seq.Count; j++) ins.Insert(start + 1 + j, seq[j]);

        // In dnlib, HandlerEnd == null means "the finally extends to the end of the method".
        // We are appending code at the end, so the leave target would land INSIDE the
        // protected region -> invalid IL -> InvalidProgramException at JIT time.
        // Insert a real boundary NOP after endfinally and point the finally's HandlerEnd
        // at it, so the appended return path sits outside the protected region.
        Instruction endMarker = Instruction.Create(OpCodes.Nop);
        ins.Add(endMarker);
        ins.Add(lout);
        ins.Add(Instruction.Create(OpCodes.Ret));
        foreach (ExceptionHandler eh in body.ExceptionHandlers)
        {
            if (eh.HandlerType == ExceptionHandlerType.Finally && eh.HandlerEnd == null)
                eh.HandlerEnd = endMarker;
        }
        body.OptimizeBranches();
        Console.WriteLine("[4] ModpackInstall dispatch restored (finally HandlerEnd fixed)");
    }

    static MethodDef CreateDispatch(TypeDef t, MethodDef orig)
    {
        foreach (MethodDef existing in t.Methods)
            if (existing.Name == "ModpackInstallDispatch")
                throw new InvalidOperationException("ModpackInstallDispatch already exists.");
        TypeSig zip = orig.Body.Variables[1].Type;
        MethodSig sig = MethodSig.CreateStatic(orig.ReturnType, new TypeSig[] {
            mod.CorLibTypes.Int32, mod.CorLibTypes.String, zip,
            mod.CorLibTypes.String, mod.CorLibTypes.String, mod.CorLibTypes.String
        });
        MethodDef md = new MethodDefUser("ModpackInstallDispatch", sig, MethodImplAttributes.IL | MethodImplAttributes.Managed,
            MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig);
        t.Methods.Add(md);
        CilBody b = new CilBody();
        b.InitLocals = false;
        md.Body = b;

        if (md.ParamDefs.Count != 6)
        {
            md.ParamDefs.Clear();
            for (int i = 0; i < 6; i++) md.ParamDefs.Add(new ParamDefUser("arg" + i, (ushort)(i + 1)));
        }

        MethodDef mCurse = FindMethod(t, "InstallPackCurseForge");
        MethodDef mModrinth = FindMethod(t, "InstallPackModrinth");
        MethodDef mHmcl = FindMethod(t, "InstallPackHMCL");
        MethodDef mMmc = FindMethod(t, "InstallPackMMC");
        MethodDef mMcbbs = FindMethod(t, "InstallPackMCBBS");
        MethodDef mLauncher = FindMethod(t, "InstallPackLauncherPack");
        MethodDef mCompress = FindMethod(t, "InstallPackCompress");

        Instruction c0 = Instruction.Create(OpCodes.Nop);
        Instruction c1 = Instruction.Create(OpCodes.Nop);
        Instruction c2 = Instruction.Create(OpCodes.Nop);
        Instruction c3 = Instruction.Create(OpCodes.Nop);
        Instruction c4 = Instruction.Create(OpCodes.Nop);
        Instruction c9 = Instruction.Create(OpCodes.Nop);
        Instruction cElse = Instruction.Create(OpCodes.Nop);

        b.Instructions.Add(Instruction.Create(OpCodes.Ldarg, md.Parameters[0]));
        b.Instructions.Add(Instruction.Create(OpCodes.Switch, new List<Instruction> { c0, c1, c2, c3, c4 }));
        b.Instructions.Add(Instruction.Create(OpCodes.Ldarg, md.Parameters[0]));
        b.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4, 9));
        b.Instructions.Add(Instruction.Create(OpCodes.Beq, c9));
        b.Instructions.Add(Instruction.Create(OpCodes.Br, cElse));

        AddCase(b, md, c0, mCurse, new int[] { 1, 2, 3, 4, 5 });
        AddCase(b, md, c1, mHmcl, new int[] { 1, 2, 3 });
        AddCase(b, md, c2, mMmc, new int[] { 1, 2, 3 });
        AddCase(b, md, c3, mMcbbs, new int[] { 1, 2, 3, 4 });
        AddCase(b, md, c4, mModrinth, new int[] { 1, 2, 3, 4, 5 });
        AddCase(b, md, c9, mLauncher, new int[] { 1, 2, 3 });
        AddCase(b, md, cElse, mCompress, new int[] { 1, 2 });
        return md;
    }

    static void AddCase(CilBody b, MethodDef md, Instruction label, MethodDef target, int[] args)
    {
        SigComparer comparer = new SigComparer();
        if (!target.IsStatic || target.MethodSig.Params.Count != args.Length ||
            !comparer.Equals(target.ReturnType, md.ReturnType))
            throw new InvalidOperationException("Unsupported install signature: " + target.FullName);
        for (int i = 0; i < args.Length; i++)
            if (!comparer.Equals(target.MethodSig.Params[i], md.Parameters[args[i]].Type))
                throw new InvalidOperationException("Unsupported install parameter: " + target.FullName);
        b.Instructions.Add(label);
        for (int i = 0; i < args.Length; i++)
            b.Instructions.Add(Instruction.Create(OpCodes.Ldarg, md.Parameters[args[i]]));
        b.Instructions.Add(Instruction.Create(OpCodes.Call, target));
        b.Instructions.Add(Instruction.Create(OpCodes.Ret));
    }

    // 5) ModpackInstall(): restore file picker + install call
    static void PatchModpackInstallNoArg()
    {
        TypeDef t = RequireType("PCL.ModModpack");
        MethodDef m0 = FindMethod(t, "ModpackInstall", 0);
        MethodDef m3 = FindMethod(t, "ModpackInstall", 3);
        RequireInstallSignature(m0, true);
        RequireInstallSignature(m3, false);
        if (!m0.HasBody) throw new InvalidOperationException("ModpackInstall() has no IL body.");

        MethodDef jm = FindMethod(RequireType("PCL.ModJava"), "ManuallyImportJava");
        if (!jm.HasBody) throw new InvalidOperationException("ManuallyImportJava has no IL body.");
        IList<Instruction> jins = jm.Body.Instructions;
        IMethod selectFile = null, oneOf = null, nullableCtor = null, firstOrDefault = null;
        for (int i = 3; i + 2 < jins.Count; i++)
        {
            IMethod im = jins[i].Operand as IMethod;
            if (im == null || im.Name.String != "SelectFile") continue;
            if (selectFile != null) throw new InvalidOperationException("Ambiguous SelectFile references.");
            selectFile = im;
            oneOf = jins[i - 2].Operand as IMethod;
            nullableCtor = jins[i - 1].Operand as IMethod;
            firstOrDefault = jins[i + 1].Operand as IMethod;
        }
        if (selectFile == null || oneOf == null || nullableCtor == null || firstOrDefault == null ||
            nullableCtor.Name != ".ctor" || firstOrDefault.Name != "FirstOrDefault")
            throw new Exception("SelectFile refs not found");

        IMethod isNullOrEmpty = FindCall("IsNullOrEmpty");
        if (isNullOrEmpty == null) throw new Exception("IsNullOrEmpty ref not found");

        CilBody b = m0.Body;
        b.Instructions.Clear();
        b.ExceptionHandlers.Clear();
        b.Variables.Clear();
        Local vFile = new Local(mod.CorLibTypes.String);
        b.Variables.Add(vFile);
        b.InitLocals = true;

        Instruction ret = Instruction.Create(OpCodes.Ret);
        b.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "Select modpack file"));
        b.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_0));
        b.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
        b.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "rar;zip;mrpack|Modpack files"));
        b.Instructions.Add(Instruction.Create(OpCodes.Call, oneOf));
        b.Instructions.Add(Instruction.Create(OpCodes.Newobj, nullableCtor));
        b.Instructions.Add(Instruction.Create(OpCodes.Call, selectFile));
        b.Instructions.Add(Instruction.Create(OpCodes.Call, firstOrDefault));
        b.Instructions.Add(Instruction.Create(OpCodes.Stloc, vFile));
        b.Instructions.Add(Instruction.Create(OpCodes.Ldloc, vFile));
        b.Instructions.Add(Instruction.Create(OpCodes.Call, isNullOrEmpty));
        b.Instructions.Add(Instruction.Create(OpCodes.Brtrue, ret));
        b.Instructions.Add(Instruction.Create(OpCodes.Ldloc, vFile));
        b.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
        b.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
        b.Instructions.Add(Instruction.Create(OpCodes.Call, m3));
        b.Instructions.Add(Instruction.Create(OpCodes.Pop));
        b.Instructions.Add(ret);
        Console.WriteLine("[5] ModpackInstall() file picker restored");
    }

    static TypeDef RequireType(string name)
    {
        TypeDef t = mod.Find(name, false);
        if (t == null) throw new InvalidOperationException("Unsupported input; type not found: " + name);
        return t;
    }

    static void RequireInstallSignature(MethodDef method, bool noArgs)
    {
        if (!method.IsStatic || method.ReturnType.ElementType != (noArgs ? ElementType.Void : ElementType.Boolean))
            throw new InvalidOperationException("Unsupported signature: " + method.FullName);
        foreach (TypeSig parameter in method.MethodSig.Params)
            if (parameter.ElementType != ElementType.String)
                throw new InvalidOperationException("Unsupported parameter: " + method.FullName);
    }

    // Branches and exception boundaries must not enter instructions being removed/replaced.
    static void RejectEntryIntoRange(CilBody body, int start, int count)
    {
        HashSet<Instruction> range = new HashSet<Instruction>();
        for (int i = start; i < start + count; i++) range.Add(body.Instructions[i]);
        foreach (Instruction instruction in body.Instructions)
        {
            Instruction branch = instruction.Operand as Instruction;
            IList<Instruction> targets = instruction.Operand as IList<Instruction>;
            if (branch != null && range.Contains(branch))
                throw new InvalidOperationException("Unsupported branch into patch region.");
            if (targets != null)
                foreach (Instruction target in targets)
                    if (range.Contains(target)) throw new InvalidOperationException("Unsupported switch into patch region.");
        }
        foreach (ExceptionHandler handler in body.ExceptionHandlers)
            if (range.Contains(handler.TryStart) || range.Contains(handler.TryEnd) ||
                range.Contains(handler.HandlerStart) || range.Contains(handler.HandlerEnd) || range.Contains(handler.FilterStart))
                throw new InvalidOperationException("Unsupported exception boundary in patch region.");
    }

    static MethodDef FindMethod(TypeDef t, string name, int parameterCount = -1)
    {
        MethodDef result = null;
        foreach (MethodDef m in t.Methods)
        {
            if (m.Name.String != name || (parameterCount >= 0 && m.MethodSig.Params.Count != parameterCount)) continue;
            if (result != null) throw new InvalidOperationException("Ambiguous method: " + t.FullName + "." + name);
            result = m;
        }
        if (result == null) throw new InvalidOperationException("Method not found: " + t.FullName + "." + name);
        return result;
    }

    static IMethod FindCall(string name)
    {
        foreach (TypeDef t in mod.GetTypes())
        {
            foreach (MethodDef m in t.Methods)
            {
                if (!m.HasBody) continue;
                foreach (Instruction ins in m.Body.Instructions)
                {
                    IMethod im = ins.Operand as IMethod;
                    if (ins.OpCode.Code == Code.Call && im != null && im.Name.String == name &&
                        im.DeclaringType.FullName == "System.String" && !im.MethodSig.HasThis &&
                        im.MethodSig.Params.Count == 1 && im.MethodSig.Params[0].ElementType == ElementType.String &&
                        im.MethodSig.RetType.ElementType == ElementType.Boolean) return im;
                }
            }
        }
        return null;
    }
}
