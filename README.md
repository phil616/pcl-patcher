# pcl-patcher

解除 Plain Craft Launcher 2（PCL2）中 `ModSecret.RestrictMcDownload` 限制的补丁工具。

PCL2 在 2.99 等版本中以 `Public Const RestrictMcDownload As Boolean = True` 的形式在**编译期**禁用了 MC 下载与整合包安装功能。由于 VB 编译器会把 `Const` 内联并折叠，受影响的功能代码在官方 exe 中根本不存在，因此本工具除了把常量改为 `False`，还会用 IL 重建被删除的逻辑。

## 原理

补丁共 5 处（基于 [dnlib](https://github.com/0xd4d/dnlib) 实现）：

| # | 位置 | 修改内容 |
|---|------|----------|
| 1 | `PCL.ModSecret.RestrictMcDownload` | 常量 `True` → `False`（元数据 Constant blob `01` → `00`） |
| 2 | `PCL.PageDownloadInstall.LoaderInit` | 把编译期折叠出来的"关闭下载"假 Loader 替换回真正的 `PCL.ModDownload::DlClientListLoader`，恢复 MC 版本列表下载 |
| 3 | `PCL.PageDownloadModify.LoadInstanceFinished` | `CardMinecraft.Visibility` 由硬编码的 `Collapsed` 改回 `Visible` |
| 4 | `PCL.ModModpack.ModpackInstall(String, String, String)` | 重建被编译器删除的整合包类型分发逻辑（CurseForge / HMCL / MMC / MCBBS / Modrinth / 带启动器压缩包 / 普通压缩包），新增 `ModpackInstallDispatch` 方法 |
| 5 | `PCL.ModModpack.ModpackInstall()` | 重建文件选择对话框入口，选中整合包后调用 3 参数重载进行安装 |

> 注：源码中 `PageDownloadCompDetail` 的相关检查在 2.99 的 exe 中不存在（该类未编译进去），无需处理；`FormMain` 拖放处理中的限制分支被折叠为 `False`，本身无害。

## 使用

```cmd
patch.exe "Plain Craft Launcher 2.exe" patched.exe
```

- 第一个参数：官方下载的原版 `Plain Craft Launcher 2.exe`
- 第二个参数：输出的补丁版 exe
- 输出必须是尚不存在的新文件，不能与输入同路径；失败时不会覆盖原版或已有输出。
- 请将发布包完整解压，保证 `patch.exe` 与 `dnlib.dll` 位于同一目录。
- 生成后可直接替换原文件运行

**本仓库不提供、也不分发任何 PCL2 的二进制文件（无论原版还是补丁版）。** 请自行从官方渠道获取 PCL2 后用本工具打补丁。

## 构建

需要 Windows + .NET Framework 4.5 或更高版本自带的 `csc.exe`、Windows PowerShell 5.1，以及首次构建时可访问 NuGet 的网络连接（无需安装 .NET SDK）。

```cmd
build.cmd
```

`build.cmd` 会自动下载固定版本的 [dnlib 4.5.0](https://www.nuget.org/packages/dnlib/4.5.0)，验证 NuGet 包及 `net45/dnlib.dll` 的 SHA256，然后编译 `src\patch.cs` 并输出 `patch.exe`。已存在的 DLL 也必须通过校验；如果此前放入了其他版本，请先移除它。

运行安全与 IL 回归测试：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test.ps1
```

检查结果和验证范围见 [SECURITY.md](SECURITY.md)。

## 自动发版

[GitHub Workflow](.github/workflows/release.yml) 在推送 `main`、创建 PR 或手动运行时构建并测试；推送 `v` 开头的版本标签时，测试通过后自动创建 GitHub Release：

```sh
git tag v1.0.0
git push origin v1.0.0
```

请先提交并推送包含工作流的代码，再创建标签。版本标签使用 `v主版本.次版本.修订版本`，例如 `v1.0.0`；`v1.1.0-rc.1` 等带后缀标签自动标记为预发布。普通分支、PR 和手动运行均不会发版。

发布附件包含 `pcl-patcher-<标签>-windows.zip` 和 `SHA256SUMS.txt`。ZIP 内为 `patch.exe`、`dnlib.dll`、README、许可证、安全说明和第三方声明，不包含 PCL2 程序。可以用 `Get-FileHash <ZIP路径> -Algorithm SHA256` 与校验文件比对。

发布使用仓库自带的 `GITHUB_TOKEN`，无需配置个人访问令牌。仓库或组织策略需允许发布作业申请 `contents: write`。已有同名 Release 时工作流会失败，不覆盖现有发布；若附件上传或公开步骤失败，应先检查留下的草稿。

## 免责声明

- 本工具仅供学习与研究，请支持原作者。
- 对使用补丁版本产生的一切后果由使用者自行承担。
- PCL2 版本更新后偏移和方法可能变化，本工具基于 **2.99.0.0** 编写并验证，其他版本不保证可用。

## License

MIT
