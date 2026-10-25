# 安装与构建手册

本手册说明如何在一台 Windows 电脑上安装本 fork 的 Files（包名 `FilesDev`），以及如何自己编译、签名、打包。版本号规则和相对上游的改动见 [FORK.md](FORK.md)。

- 只想在另一台电脑上用：看「一、安装」和「二、从官方版迁移」，不需要拉代码、不需要编译。
- 想在这台电脑上改代码并打包：看「三、构建打包」。

## 一、安装

### 1. 准备文件

从已经打好包的电脑拷两个文件，放在同一个文件夹里（位于仓库的 `artifacts\AppxPackages\Files.App_<版本>_Test\` 下）：

- `Files.App_<版本>_x64.msixbundle`：安装包
- `Files.App_<版本>_x64.cer`：签名证书

### 2. 信任证书（每台电脑只需一次）

在这个文件夹里打开**管理员 PowerShell**：

```powershell
Import-Certificate -FilePath ".\Files.App_<版本>_x64.cer" -CertStoreLocation Cert:\LocalMachine\TrustedPeople
```

证书是自签名的，到期日 2027-10-25。证书更换后要重新导入新的 `.cer`。

### 3. 安装 / 升级

双击 `.msixbundle`，在「应用安装程序」里点「安装」或「更新」。

- 升级时配置会保留。
- 只能装比已安装版本**更高**的版本号；版本号相同但内容不同的包会被拒绝。

## 二、从官方版迁移

如果这台电脑装着官方 Files（商店版，包名 `Files`），新版会和它并排存在，配置各自独立。按下面顺序迁移配置并卸载官方版。

### 1. 先处理「设为默认文件管理器」

如果官方版里打开过 设置 → 高级 →「设为默认文件管理器」，**卸载官方版之前**先在官方版里把它关掉。否则卸载后 Win+E 和双击文件夹可能报「找不到应用程序」。没打开过可跳过。

### 2. 迁移配置

1. 先打开新版一次，让它生成自己的配置目录。
2. **彻底退出两个 Files**（开着「关闭时最小化到托盘」的，要在托盘图标上右键退出）。
3. 在普通 PowerShell 里执行：

```powershell
$old = "$env:LOCALAPPDATA\Packages\Files_1y0xx7n9077q4\LocalState\settings"
$new = "$env:LOCALAPPDATA\Packages\FilesDev_ykqwq8d6ps0ag\LocalState\settings"
$bak = "$env:USERPROFILE\Desktop\Files-official-settings"
if (Get-Process | Where-Object { $_.Path -like '*WindowsApps\Files*' }) { throw "Files 还在运行，请先彻底退出" }
New-Item -ItemType Directory -Force $bak, $new | Out-Null
foreach ($n in 'user_settings.json', 'filetags.json') {
    if (Test-Path "$old\$n") {
        [IO.File]::WriteAllBytes("$bak\$n", [IO.File]::ReadAllBytes("$old\$n"))
        if (Test-Path "$new\$n") { [IO.File]::WriteAllBytes("$new\$n.bak", [IO.File]::ReadAllBytes("$new\$n")) }
        [IO.File]::WriteAllBytes("$new\$n", [IO.File]::ReadAllBytes("$old\$n"))
        "{0}: {1}" -f $n, ((Get-FileHash "$old\$n").Hash -eq (Get-FileHash "$new\$n").Hash)
    }
}
```

每个文件输出 `True` 即成功。说明：

- 迁移的是 `user_settings.json`（全部设置）和 `filetags.json`（文件标签）。窗口位置等包级设置不迁移，新版首次打开时会重置一次。
- 旧配置备份在桌面的 `Files-official-settings`，新版原来的文件保留为同目录下的 `.bak`。
- 应用包里的文件可能带 Application Protected 加密，`Copy-Item` 会报「The specified file could not be encrypted」，所以这里按字节复制。
- 固定到快速访问的文件夹存在系统 Shell 里，不需要迁移。

### 3. 卸载官方版

打开新版，确认设置、标签都正确后执行：

```powershell
Get-AppxPackage Files | Remove-AppxPackage
```

卸载会删除官方版的数据目录，所以务必先完成上一步。

### 4. 重新设为默认（可选）

在新版里打开 设置 → 高级 →「设为默认文件管理器」，弹出 UAC 时点「是」。之后用 Win+E 和双击文件夹验证。

「使用 Files 作为打开文件对话框」在本 fork 中固定禁用（打包流程不编译对话框组件）。

## 三、构建打包

### 1. 环境

| 组件 | 要求 |
|---|---|
| Visual Studio Build Tools | **2026**（MSBuild 18）。VS 2022 的 MSBuild 无法解析 .NET 10 SDK |
| 工作负载 | .NET 桌面生成工具、C++ 生成工具（MSVC v145） |
| Windows SDK | 10.0.26100 |
| .NET SDK | 10.x（`dotnet --list-sdks` 确认） |

一条命令安装（安装路径按需修改；`--nocache` 装完删除下载缓存）：

```powershell
winget install --id Microsoft.VisualStudio.BuildTools -e --source winget --override "--passive --wait --norestart --nocache --installPath F:\Software\VS\BuildTools2026 --add Microsoft.VisualStudio.Workload.ManagedDesktopBuildTools --add Microsoft.VisualStudio.Workload.VCTools --add Microsoft.VisualStudio.Component.VC.Tools.x86.x64 --add Microsoft.VisualStudio.Component.Windows11SDK.26100 --includeRecommended"
```

空间提示：VS 共享组件和 Windows SDK 固定装在 C 盘，C 盘至少预留约 8 GB。C 盘紧张时，可以把 NuGet 包缓存（约 5 GB）移到其他盘，并设置用户环境变量 `NUGET_PACKAGES` 指向新位置。

### 2. 签名证书

打包用的证书是 `artifacts\FilesApp_SelfSigned.pfx`，已被 gitignore 忽略，**不在仓库里**。

- 已有证书：从原来的电脑拷到新电脑仓库的 `artifacts\` 下。**所有电脑要用同一张证书**，否则彼此打的包不能覆盖升级。
- 首次生成（会同时存入当前用户的证书存储）：

```powershell
New-Item -ItemType Directory -Force artifacts | Out-Null
.\.github\scripts\Generate-SelfCertPfx.ps1 -Destination "$PWD\artifacts\FilesApp_SelfSigned.pfx"
```

### 3. 递增版本号

打包前按 [FORK.md](FORK.md) 的规则修改 `src/Files.App/Package.appxmanifest` 中的 `Version`，并在 FORK.md 的改动表里补一行。版本号必须大于所有已安装电脑上的版本。

### 4. 编译与打包

在仓库根目录打开 PowerShell，依次执行下面的脚本（把 `$vs` 改成实际的 Build Tools 安装路径）：

```powershell
$vs = "F:\Software\VS\BuildTools2026"
$repo = (Get-Location).Path

# NativeAOT 链接时需要 vswhere.exe，必须在 PATH 里
$env:PATH = "C:\Program Files (x86)\Microsoft Visual Studio\Installer;" + $env:PATH
& "$vs\Common7\Tools\Launch-VsDevShell.ps1" -Arch amd64 -HostArch amd64 -SkipAutomaticLocation | Out-Null
Set-Location $repo

# 1) 编译 C++ Launcher（「设为默认文件管理器」依赖它），产物会自动复制到 src\Files.App\Assets\FilesOpenDialog\
msbuild src\Files.App.Launcher\Files.App.Launcher.vcxproj -t:Restore -p:RestorePackagesConfig=true "-p:SolutionDir=$repo\\" -p:Configuration=Release -p:Platform=x64 -v:quiet
msbuild src\Files.App.Launcher\Files.App.Launcher.vcxproj -t:Build "-p:SolutionDir=$repo\\" -p:Configuration=Release -p:Platform=x64 -v:quiet -clp:ErrorsOnly

# 2) 编译并打包（先 Clean 再 Build）
msbuild -restore src\Files.App\Files.App.csproj "-t:Clean;Build" -p:Configuration=Release -p:Platform=x64 -p:AppxBundlePlatforms=x64 -p:AppxBundle=Always -p:GenerateAppxPackageOnBuild=true -p:UapAppxPackageBuildMode=SideloadOnly "-p:AppxPackageDir=$repo\artifacts\AppxPackages\\" -p:AppxPackageSigningEnabled=true "-p:PackageCertificateKeyFile=$repo\artifacts\FilesApp_SelfSigned.pfx" -p:PackageCertificatePassword="" -p:PackageCertificateThumbprint="" -v:quiet -clp:ErrorsOnly
```

产物在 `artifacts\AppxPackages\Files.App_<版本>_Test\`，包含 `.msixbundle` 和 `.cer`，按「一、安装」安装即可。

### 5. 验证 Launcher 已打进包

```powershell
Test-Path src\Files.App\Assets\FilesOpenDialog\Files.App.Launcher.exe
```

返回 `False` 说明第 1) 步没成功，这样打出的包里「设为默认文件管理器」不可用。

## 四、常见问题

| 现象 | 原因 / 处理 |
|---|---|
| `无法解析 SDK "Microsoft.NET.Sdk"` | 用了 VS 2022 的 MSBuild，改用 VS 2026 Build Tools |
| `Could not read the Windows SDK's Platform.xml` | 未安装 Windows SDK 10.0.26100 |
| `'vswhere.exe' 不是内部或外部命令`（链接 Files.App.Server 时） | 未把 VS Installer 目录加入 PATH，见构建脚本第一行 |
| Launcher 编译报缺少 `Microsoft.Windows.ImplementationLibrary` | 还原时漏了 `-p:RestorePackagesConfig=true` 和 `-p:SolutionDir=` |
| 双击安装包提示无法验证发布者 | 证书没有导入到「本地计算机 → 受信任的人」 |
| 安装提示已安装相同或更高版本 | 递增 `Version` 后重新打包 |
| Win+E 报「找不到应用程序」 | 检查 `%LOCALAPPDATA%\Files\Files.App.Launcher.exe` 是否存在；在设置里关闭再打开「设为默认文件管理器」 |

运行日志位置：`%LOCALAPPDATA%\Packages\FilesDev_ykqwq8d6ps0ag\LocalState\debug_unprivileged.log`。
