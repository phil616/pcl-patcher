# 安全检查记录

本次检查范围为 `src/patch.cs`、构建脚本和自动发布流程。静态检查未发现补丁工具主动执行输入程序集、启动外部命令或进行网络通信的代码；构建脚本需要访问 NuGet 下载 dnlib。这不是完整的安全认证。

## 已修复的问题

| 问题 | 影响 | 处理 |
| --- | --- | --- |
| 直接写入用户指定路径 | 覆盖原版或已有文件，写入失败可能留下损坏输出 | 拒绝输入输出同路径及已有输出；使用同目录临时文件写入，再以不覆盖方式重命名；失败清理临时文件 |
| 缺少参数和目标结构检查 | 参数错误、版本变化或重复补丁时异常退出，部分宽泛匹配可能修改错误位置 | 检查参数、路径、Boolean 常量、重载签名、局部变量和安装函数参数；对多处匹配及已打补丁输入拒绝处理 |
| 修改 IL 时未检查跳转入口 | 其他分支或异常处理边界可能进入被改写区域，产生错误 IL | 拒绝从改写区外部跳入内部的分支、switch 目标及区域内异常处理边界；允许随整个代码块一起移除的内部跳转；扩展短跳转后重新优化 |
| 手动引入未固定版本的 dnlib | 构建结果取决于本地 DLL 来源及版本 | 固定 dnlib 4.5.0，下载后验证 NuGet 包及 DLL 的 SHA256；已有 DLL 不匹配时拒绝构建 |

自动发布使用完整 commit SHA 固定官方 Actions，普通 CI 只有仓库读取权限；标签构建为生成证明单独获得下述权限。发布作业只下载构建产物并调用 GitHub CLI，不运行仓库脚本。标签通过环境变量传递，打包前校验格式，发布前核对标签仍指向本次构建提交。附件全部上传到草稿后再公开发布，并附带 SHA256 校验文件。这些措施参考 [GitHub Actions 安全建议](https://docs.github.com/en/actions/reference/security/secure-use)。

## 供应链发布配置

- 普通分支、PR 和手动 CI 只有 `contents: read`。标签构建作业为生成证明单独获得 `id-token: write`、`attestations: write`，发布作业为验证和上传获得 `attestations: read`、`contents: write`。构建代码不会在普通 PR 中获得证明写入权限。
- 固定版本的官方 `actions/attest` 为最终 ZIP 和 SBOM 生成 SLSA provenance，并为 ZIP 生成 SPDX 2.3 SBOM attestation。签名使用 OIDC 和 Sigstore，不配置长期签名密钥。两个证明 bundle 随 Release 分发，也上传至 GitHub 的证明服务。
- 发布作业不检出代码、不执行仓库脚本；它先检查全部附件摘要，再通过 `gh attestation verify` 限定仓库、签发工作流、源码标签、源码提交及托管 runner。ZIP 和 SBOM 都须通过构建证明验证；签名 SBOM 的 predicate 还必须与外部 SBOM 附件相同。任何验证失败都会在创建 Release 前停止。
- SBOM 依据实际分发文件生成，包含 SHA256、SPDX 所需的 SHA1 文件摘要及包校验码。SHA1 仅用于 SPDX 清单兼容，产物完整性验证使用 SHA256。文档明确标记 dnlib 为上游预编译依赖；本仓库的证明不声称重新构建或审计了 dnlib。
- dnlib 使用标准 `packages.config` 声明，作为构建和 SBOM 的依赖版本依据；`dependencies.lock.json` 同时固定 NuGet 包及实际 DLL 的 SHA256，两份记录不一致时拒绝构建。依赖版本、摘要和 Actions 的 commit SHA 由维护者手动审阅更新。
- 2026-10-20 已通过 GitHub API 启用并回读确认 `phil616/pcl-patcher` 的不可变发布。它保护之后公开的发布附件和关联标签，不会追溯锁定历史发布。发布后工作流还会运行 `gh release verify` 和 ZIP 的 `verify-asset`。

参考：[Artifact attestations](https://docs.github.com/en/actions/concepts/security/artifact-attestations)、[Immutable releases](https://docs.github.com/en/code-security/concepts/supply-chain-security/immutable-releases)、[依赖图](https://docs.github.com/en/code-security/concepts/supply-chain-security/dependency-graph)。当前配置不宣称获得 GitHub 安全认证或达到 SLSA Build Level 3。由于 Actions lockfile 仍处于公开预览，本项目暂时保留完整 commit SHA 固定和定期更新。

## 验证与限制

- `scripts/test.ps1` 生成合成程序集，覆盖文件保护、错误输入、歧义模式、外部跳转进入改写区、VB 委托缓存内部跳转、继承的 CardMinecraft 属性、参数/局部变量不匹配、五处补丁和重复补丁拒绝；在 .NET Framework 中实际执行返回 `LoaderCombo<String>` 的合成整合包分发和 try/finally 路径。
- `scripts/test-release.ps1` 检查实际 ZIP 文件清单、SBOM 组件和关系、文件摘要、包校验码、内外 SBOM 一致性、附件校验文件、重复输出保护、依赖版本与锁定记录不一致及 DLL 篡改拒绝。测试产物保留在 `obj` 下供检查。
- 已在本地用 SPDX 官方 JSON schema 验证生成的 SPDX 2.3 文档，并使用 actionlint 检查工作流语法。完整 OIDC 签发、Sigstore 验证及首次不可变 Release 发布需要工作流在 GitHub 上实际运行；本地测试不能替代这一验证。
- 2026-10-20 已使用本机提供的 PCL2 2.99.0.0 文件复现并修复三处过严检查：`LoaderInit` 的 VB 委托缓存内部跳转、从 `PageDownloadInstall` 继承的 CardMinecraft getter，以及实际返回 `LoaderCombo<String>` 的整合包安装方法。五处补丁均已成功写入；与安全检查改动前的 `e0b3ff4` 补丁工具输出比对，统一长短跳转形式后全部 6,340 个方法的 IL、局部变量及异常处理区域一致，输入文件 SHA256 未变。没有启动真实启动器界面或实测下载/安装，也未验证其他版本。
- 输入文件本身及补丁输出仍是可执行程序。只处理可信的官方原版；补丁不会审计原程序或整合包内容，也不会保留原程序的数字签名有效性。
- 固定哈希防止下载内容变化，不代表 dnlib 没有漏洞。解析畸形程序集仍可能消耗大量资源或触发第三方解析器问题。
- 当前回归测试不模拟磁盘写满、文件系统故障或并发创建输出的竞争场景；输出发布使用不覆盖的 `File.Move` 保护已有文件。
- 此配置不为 Windows EXE 提供 Authenticode 代码签名，也不替代代码审计和漏洞修复。
