using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using MA = dnlib.DotNet.MethodAttributes;
using TA = dnlib.DotNet.TypeAttributes;
using FA = dnlib.DotNet.FieldAttributes;

// Synthetic assemblies only: no PCL binaries are downloaded or redistributed.
static class SecurityTests
{
    static string patcher;
    static string work;
    static string lastOutput;
    static int checks;

    static int Main(string[] args)
    {
        patcher = Path.GetFullPath(args[0]);
        work = Path.Combine(Path.GetDirectoryName(patcher), "obj", "security-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            Check(Run("") != 0, "missing arguments");
            Check(Run("--help") == 0, "help");
            string input = Path.Combine(work, "original file.dll");
            string output = Path.Combine(work, "patched file.dll");
            Check(Run(Q(input) + " " + Q(output)) != 0 && !File.Exists(output), "missing input");
            File.WriteAllText(input, "not a managed assembly");
            Check(Run(Q(input) + " " + Q(output)) != 0 && !File.Exists(output), "invalid PE");
            Fixture(input, "valid");
            string originalHash = Hash(input);
            Check(Run(Q(input) + " " + Q(input)) != 0 && Hash(input) == originalHash, "same path preserves input");
            File.WriteAllText(output, "keep this file");
            Check(Run(Q(input) + " " + Q(output)) != 0 && File.ReadAllText(output) == "keep this file", "existing output preserved");
            File.Delete(output);
            Check(Run(Q(input) + " " + Q(work)) != 0, "directory output rejected");
            Check(Run(Q(input) + " " + Q(Path.Combine(work, "missing", "out.dll"))) != 0, "missing parent rejected");

            foreach (string mode in new[] { "already-patched", "ambiguous-loader", "branch-into-loader", "switch-into-loader", "wrong-installer-signature", "wrong-installer-return", "wrong-result-local", "wrong-locals" })
            {
                Fixture(input, mode);
                string hash = Hash(input);
                Check(Run(Q(input) + " " + Q(output)) != 0 && !File.Exists(output) && Hash(input) == hash, mode);
                Check(Directory.GetFiles(work, ".pcl-patch-*.tmp").Length == 0, mode + " leaves no temporary output");
            }

            Fixture(input, "internal-loader-branches");
            originalHash = Hash(input);
            Check(Run(Q(input) + " " + Q(output)) == 0 && Hash(input) == originalHash, "VB delegate-cache internal branches are replaced safely");
            using (ModuleDefMD result = ModuleDefMD.Load(output))
            {
                MethodDef loader = result.Find("PCL.PageDownloadInstall", false).FindMethod("LoaderInit");
                Check(loader.Body.Instructions.Count(i => i.OpCode.Code == Code.Ldsfld) == 1 &&
                    !loader.Body.Instructions.Any(i => i.Operand is Instruction), "internal branches disappear with replaced loader instructions");
            }
            Assembly.Load(File.ReadAllBytes(output)).GetType("PCL.PageDownloadInstall").GetMethod("LoaderInit").Invoke(null, null);
            checks++;
            File.Delete(output);

            Fixture(input, "valid");
            originalHash = Hash(input);
            Check(Run(Q(input) + " " + Q(output)) == 0 && File.Exists(output), "complete patch succeeds");
            Check(Hash(input) == originalHash, "successful patch preserves input");
            using (ModuleDefMD result = ModuleDefMD.Load(output))
            {
                Check(!(bool)result.Find("PCL.ModSecret", false).FindField("RestrictMcDownload").Constant.Value, "constant patched");
                MethodDef install = result.Find("PCL.ModModpack", false).Methods.Single(m => m.Name == "ModpackInstall" && m.MethodSig.Params.Count == 3);
                Check(install.Body.ExceptionHandlers.Count == 1, "patched method retains its finally handler (actual: " + install.Body.ExceptionHandlers.Count + ")");
                ExceptionHandler handler = install.Body.ExceptionHandlers.Single();
                Check(handler.HandlerEnd != null && handler.HandlerEnd.OpCode.Code == Code.Nop, "finally boundary closed before return");
                Check(result.Find("PCL.PageDownloadModify", false).FindMethod("LoadInstanceFinished").Body.Instructions[1].OpCode.Code == Code.Ldc_I4_0, "inherited CardMinecraft visibility patched");
            }
            // Only our own generated assembly is executed. The CLR checks the rewritten IL.
            Assembly fixture = Assembly.Load(File.ReadAllBytes(output));
            Type modpack = fixture.GetType("PCL.ModModpack");
            MethodInfo dispatch = modpack.GetMethod("ModpackInstallDispatch", BindingFlags.NonPublic | BindingFlags.Static);
            int[] packTypes = { 0, 1, 2, 3, 4, 9, -1, 99 };
            string[] expected = { "InstallPackCurseForge", "InstallPackHMCL", "InstallPackMMC", "InstallPackMCBBS", "InstallPackModrinth", "InstallPackLauncherPack", "InstallPackCompress", "InstallPackCompress" };
            for (int i = 0; i < packTypes.Length; i++)
            {
                object value = dispatch.Invoke(null, new object[] { packTypes[i], null, null, null, null, null });
                Check(value != null && (string)value.GetType().GetField("Kind").GetValue(value) == expected[i], "reference-returning dispatch type " + packTypes[i]);
            }
            object installed = modpack.GetMethod("ModpackInstall", new[] { typeof(string), typeof(string), typeof(string) }).Invoke(null, new object[] { null, null, null });
            Check(installed != null && (string)installed.GetType().GetField("Kind").GetValue(installed) == "InstallPackCurseForge", "patched try/finally returns LoaderCombo<String>");
            modpack.GetMethod("ModpackInstall", Type.EmptyTypes).Invoke(null, null);
            checks++;
            fixture.GetType("PCL.PageDownloadInstall").GetMethod("LoaderInit").Invoke(null, null);
            checks++;
            Check(Run(Q(output) + " " + Q(Path.Combine(work, "twice.dll"))) != 0 && !File.Exists(Path.Combine(work, "twice.dll")), "repeat patch rejected");
            Check(Directory.GetFiles(work, ".pcl-patch-*.tmp").Length == 0, "no temporary output remains");
            Console.WriteLine("PASS: " + checks + " security and IL regression checks.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        finally
        {
            // This uniquely named directory is created above, inside the repository's obj directory.
            foreach (string path in Directory.GetFiles(work)) File.Delete(path);
            Directory.Delete(work);
        }
    }

    static string Q(string value) { return "\"" + value + "\""; }
    static string Hash(string path)
    {
        using (SHA256 sha = SHA256.Create())
        using (Stream stream = File.OpenRead(path))
            return BitConverter.ToString(sha.ComputeHash(stream));
    }
    static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAILED: " + name + Environment.NewLine + lastOutput);
        checks++;
    }
    static int Run(string arguments)
    {
        ProcessStartInfo info = new ProcessStartInfo(patcher, arguments);
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        using (Process process = Process.Start(info))
        {
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30000)) { process.Kill(); throw new Exception("Patcher timed out."); }
            lastOutput = stdout.Result + stderr.Result;
            return process.ExitCode;
        }
    }

    static TypeDef AddType(ModuleDef module, string name)
    {
        TypeDef type = new TypeDefUser("PCL", name, module.CorLibTypes.Object.TypeDefOrRef);
        type.Attributes = TA.Public | TA.Class;
        module.Types.Add(type);
        return type;
    }
    static MethodDef Method(TypeDef type, string name, TypeSig ret, params TypeSig[] parameters)
    {
        MethodDef method = new MethodDefUser(name, MethodSig.CreateStatic(ret, parameters), MA.Public | MA.Static);
        method.Body = new CilBody();
        type.Methods.Add(method);
        return method;
    }
    static void Emit(MethodDef method, params Instruction[] instructions)
    {
        foreach (Instruction instruction in instructions) method.Body.Instructions.Add(instruction);
    }
    static MethodDef Constructor(TypeDef type, params TypeSig[] parameters)
    {
        MethodDef method = new MethodDefUser(".ctor", MethodSig.CreateInstance(type.Module.CorLibTypes.Void, parameters), MA.Public | MA.SpecialName | MA.RTSpecialName);
        method.Body = new CilBody();
        type.Methods.Add(method);
        Emit(method, Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Call, type.Module.Import(typeof(object).GetConstructor(Type.EmptyTypes))), Instruction.Create(OpCodes.Ret));
        return method;
    }

    static void Fixture(string path, string mode)
    {
        using (ModuleDefUser module = new ModuleDefUser("Fixture.dll"))
        {
            module.Kind = ModuleKind.Dll;
            new AssemblyDefUser("PatcherFixture", new Version(2, 99, 0, 0)).Modules.Add(module);
            TypeSig str = module.CorLibTypes.String, boolean = module.CorLibTypes.Boolean;
            TypeSig obj = module.CorLibTypes.Object, integer = module.CorLibTypes.Int32, none = module.CorLibTypes.Void;
            TypeDef secret = AddType(module, "ModSecret");
            secret.Fields.Add(new FieldDefUser("RestrictMcDownload", new FieldSig(boolean), FA.Public | FA.Static | FA.Literal | FA.HasDefault) { Constant = new ConstantUser(mode != "already-patched") });

            TypeDef loader = AddType(module, "LoaderTask");
            bool internalBranches = mode == "internal-loader-branches";
            MethodDef ctor = Constructor(loader, internalBranches ? new[] { str, obj, obj, integer } : Enumerable.Repeat(str, 12).ToArray());
            FieldDef dl = new FieldDefUser("DlClientListLoader", new FieldSig(loader.ToTypeSig()), FA.Public | FA.Static);
            AddType(module, "ModDownload").Fields.Add(dl);
            TypeDef installPage = AddType(module, "PageDownloadInstall");
            MethodDef init = Method(installPage, "LoaderInit", none);
            for (int block = 0; block < (mode == "ambiguous-loader" ? 2 : 1); block++)
            {
                Emit(init, Instruction.Create(OpCodes.Ldstr, "restricted"));
                if (internalBranches)
                {
                    FieldDef cache = new FieldDefUser("CachedDelegate", new FieldSig(module.ImportAsTypeSig(typeof(Action))), FA.Public | FA.Static);
                    installPage.Fields.Add(cache);
                    MethodDef callback = Method(installPage, "RestrictedCallback", none);
                    Emit(callback, Instruction.Create(OpCodes.Ret));
                    Instruction create = Instruction.Create(OpCodes.Ldnull);
                    Instruction done = Instruction.Create(OpCodes.Ldnull);
                    Emit(init, Instruction.Create(OpCodes.Ldsfld, cache), Instruction.Create(OpCodes.Brfalse_S, create),
                        Instruction.Create(OpCodes.Ldsfld, cache), Instruction.Create(OpCodes.Br_S, done), create,
                        Instruction.Create(OpCodes.Ldftn, callback),
                        Instruction.Create(OpCodes.Newobj, module.Import(typeof(Action).GetConstructor(new[] { typeof(object), typeof(IntPtr) }))),
                        Instruction.Create(OpCodes.Dup), Instruction.Create(OpCodes.Stsfld, cache), done, Instruction.Create(OpCodes.Ldc_I4_2));
                }
                else for (int i = 0; i < 11; i++) Emit(init, Instruction.Create(OpCodes.Ldnull));
                Emit(init, Instruction.Create(OpCodes.Newobj, ctor), Instruction.Create(OpCodes.Pop));
            }
            Emit(init, Instruction.Create(OpCodes.Ret));
            if (mode == "branch-into-loader")
            {
                Instruction target = init.Body.Instructions[1];
                init.Body.Instructions.Insert(0, Instruction.Create(OpCodes.Br, target));
                init.Body.Instructions.Insert(0, Instruction.Create(OpCodes.Ldstr, "alternate entry"));
            }
            if (mode == "switch-into-loader")
            {
                Instruction target = init.Body.Instructions[1];
                init.Body.Instructions.Insert(0, Instruction.Create(OpCodes.Pop));
                init.Body.Instructions.Insert(0, Instruction.Create(OpCodes.Switch, new[] { target }));
                init.Body.Instructions.Insert(0, Instruction.Create(OpCodes.Ldc_I4_0));
                init.Body.Instructions.Insert(0, Instruction.Create(OpCodes.Ldstr, "alternate entry"));
            }

            TypeDef visibility = new TypeDefUser("System.Windows", "Visibility", module.CorLibTypes.GetTypeRef("System", "Enum"));
            visibility.Attributes = TA.Public | TA.Sealed;
            visibility.Fields.Add(new FieldDefUser("value__", new FieldSig(integer), FA.Public | FA.SpecialName | FA.RTSpecialName));
            module.Types.Add(visibility);
            TypeDef card = AddType(module, "Card");
            MethodDef setter = new MethodDefUser("set_Visibility", MethodSig.CreateInstance(none, visibility.ToTypeSig()), MA.Public);
            setter.Body = new CilBody();
            card.Methods.Add(setter);
            Emit(setter, Instruction.Create(OpCodes.Ret));
            TypeDef modify = AddType(module, "PageDownloadModify");
            modify.BaseType = installPage;
            MethodDef getter = Method(installPage, "get_CardMinecraft", card.ToTypeSig());
            Emit(getter, Instruction.Create(OpCodes.Ldnull), Instruction.Create(OpCodes.Ret));
            MethodDef finished = Method(modify, "LoadInstanceFinished", none);
            Emit(finished, Instruction.Create(OpCodes.Call, getter), Instruction.Create(OpCodes.Ldc_I4_2), Instruction.Create(OpCodes.Callvirt, setter), Instruction.Create(OpCodes.Ret));

            TypeDef pack = AddType(module, "ModModpack");
            TypeDef resultType = new TypeDefUser("", "LoaderCombo`1", module.CorLibTypes.Object.TypeDefOrRef);
            resultType.Attributes = TA.NestedPublic | TA.Class;
            AddType(module, "ModLoader").NestedTypes.Add(resultType);
            resultType.GenericParameters.Add(new GenericParamUser(0, GenericParamAttributes.NonVariant, "T"));
            FieldDef kind = new FieldDefUser("Kind", new FieldSig(str), FA.Public);
            resultType.Fields.Add(kind);
            MethodDef resultCtor = Constructor(resultType, str);
            resultCtor.Body.Instructions.RemoveAt(resultCtor.Body.Instructions.Count - 1);
            Emit(resultCtor, Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldarg_1), Instruction.Create(OpCodes.Stfld, kind), Instruction.Create(OpCodes.Ret));
            TypeSig resultSig = new GenericInstSig(new ClassSig(resultType), str);
            IMethod newResult = new MemberRefUser(module, ".ctor", MethodSig.CreateInstance(none, str), new TypeSpecUser(resultSig));
            MethodDef install = Method(pack, "ModpackInstall", resultSig, str, str, str);
            install.Body.InitLocals = true;
            foreach (TypeSig local in new[] { mode == "wrong-result-local" ? boolean : resultSig, obj, str, obj, mode == "wrong-locals" ? str : integer }) install.Body.Variables.Add(new Local(local));
            Instruction begin = Instruction.Create(OpCodes.Ldstr, "disabled qwq");
            Instruction end = Instruction.Create(OpCodes.Nop);
            Emit(install, begin, Instruction.Create(OpCodes.Newobj, module.Import(typeof(Exception).GetConstructor(new[] { typeof(string) }))), Instruction.Create(OpCodes.Throw), end, Instruction.Create(OpCodes.Endfinally));
            install.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally) { TryStart = begin, TryEnd = end, HandlerStart = end, HandlerEnd = null });
            MethodDef noArgs = Method(pack, "ModpackInstall", none);
            Emit(noArgs, Instruction.Create(OpCodes.Ret));
            string[] installers = { "InstallPackCurseForge", "InstallPackHMCL", "InstallPackMMC", "InstallPackMCBBS", "InstallPackModrinth", "InstallPackLauncherPack", "InstallPackCompress" };
            int[] counts = { 5, 3, 3, 4, 5, 3, 2 };
            TypeSig[] installParams = { str, obj, str, str, str };
            for (int i = 0; i < installers.Length; i++)
            {
                bool wrongReturn = mode == "wrong-installer-return" && i == 0;
                MethodDef target = Method(pack, installers[i], wrongReturn ? boolean : resultSig, installParams.Take(counts[i]).ToArray());
                if (mode == "wrong-installer-signature" && i == 0) target.MethodSig.Params[0] = integer;
                if (wrongReturn) Emit(target, Instruction.Create(OpCodes.Ldc_I4_1), Instruction.Create(OpCodes.Ret));
                else Emit(target, Instruction.Create(OpCodes.Ldstr, installers[i]), Instruction.Create(OpCodes.Newobj, newResult), Instruction.Create(OpCodes.Ret));
            }

            TypeDef java = AddType(module, "ModJava");
            MethodDef oneOf = Method(java, "OneOf", integer, str);
            Emit(oneOf, Instruction.Create(OpCodes.Ldc_I4_0), Instruction.Create(OpCodes.Ret));
            TypeDef filter = AddType(module, "Filter");
            MethodDef filterCtor = Constructor(filter, integer);
            MethodDef select = Method(java, "SelectFile", new SZArraySig(str), str, integer, obj, filter.ToTypeSig());
            Emit(select, Instruction.Create(OpCodes.Ldnull), Instruction.Create(OpCodes.Ret));
            MethodDef first = Method(java, "FirstOrDefault", str, new SZArraySig(str));
            Emit(first, Instruction.Create(OpCodes.Ldnull), Instruction.Create(OpCodes.Ret));
            MethodDef import = Method(java, "ManuallyImportJava", none);
            Emit(import, Instruction.Create(OpCodes.Ldstr, "title"), Instruction.Create(OpCodes.Ldc_I4_0), Instruction.Create(OpCodes.Ldnull), Instruction.Create(OpCodes.Ldstr, "filter"),
                Instruction.Create(OpCodes.Call, oneOf), Instruction.Create(OpCodes.Newobj, filterCtor), Instruction.Create(OpCodes.Call, select), Instruction.Create(OpCodes.Call, first), Instruction.Create(OpCodes.Pop),
                Instruction.Create(OpCodes.Ldnull), Instruction.Create(OpCodes.Call, module.Import(typeof(string).GetMethod("IsNullOrEmpty"))), Instruction.Create(OpCodes.Pop), Instruction.Create(OpCodes.Ret));
            module.Write(path);
        }
    }
}
