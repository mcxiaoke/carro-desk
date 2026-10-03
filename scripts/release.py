#
# CarroDesk Release 打包脚本
#
# 流程：
#   1. 从 Directory.Build.props 解析 Major.Minor.Patch 版本号；
#   2. dotnet Release 构建 GUI（src/CarroDesk）与 CLI（cli/CarroDesk.Cli）；
#   3. 把 CLI 单 exe 复制进 GUI 输出目录（两者同时发布，GUI 目录即完整分发包）；
#   4. 打包 zip 到 release/<version>/CarroDesk-<version>-windows-x64.zip；
#   5. 编译 Inno Setup 安装包到 release/<version>/CarroDesk-<version>-setup.exe（per-user，免 UAC）；
#   6. 生成 SHA256SUMS.txt 校验清单。
#

USAGE = """用法：
  python scripts/release.py                     # 用 Directory.Build.props 里的版本号
  python scripts/release.py 1.2.0               # 指定版本号
  python scripts/release.py --no-setup           # 跳过安装包（未装 Inno Setup 时可用）
  python scripts/release.py --setup-only        # 只出安装包，跳过 zip
  python scripts/release.py --help              # 显示本帮助

产物（release/<version>/）：
  CarroDesk-<version>-windows-x64.zip          便携版压缩包
  CarroDesk-<version>-setup.exe                Inno Setup 安装包（per-user，免 UAC）
  SHA256SUMS.txt                               校验清单

安装包特性：
  * 默认安装到 %LOCALAPPDATA%\\Programs\\CarroDesk，无需管理员权限，全程免 UAC 弹窗
  * 安装/升级/卸载前自动检测并提示结束 CarroDesk.exe 与 CarroDesk.Cli.exe（含子进程树）
  * 自动继承已有安装路径（支持旧版 NSIS 与 Inno Setup 路径平滑升级）
  * 安装时可选数据模式：漫游（%AppData%\\CarroDesk）或便携（安装目录 app_data）
  * 便携模式纯绿色零系统残留；切换模式时支持配置安全迁移
  * 升级只覆盖程序文件，配置与任务数据保持不变；卸载时保留用户数据

依赖：Inno Setup 6+（scoop install inno-setup 或 iscc 在 PATH 中）。未安装时自动跳过安装包，zip 不受影响。
"""

import hashlib
import io
import os
import re
import shutil
import subprocess
import sys
import zipfile
from pathlib import Path

if sys.platform == "win32":
    sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")
    sys.stderr = io.TextIOWrapper(sys.stderr.buffer, encoding="utf-8", errors="replace")

ROOT = Path(__file__).resolve().parent.parent
GUI_PROJECT = ROOT / "src" / "CarroDesk.csproj"
CLI_PROJECT = ROOT / "cli" / "CarroDesk.Cli" / "CarroDesk.Cli.csproj"
GUI_OUT = ROOT / "src" / "bin" / "Release" / "net48"
CLI_OUT = ROOT / "cli" / "CarroDesk.Cli" / "bin" / "Release" / "net48"
ISS_SCRIPT = ROOT / "scripts" / "installer" / "CarroDesk.iss"
APP_ICON = ROOT / "src" / "Assets" / "Icon.ico"

# 运行时生成、不应进入发布包的文件/目录（相对 GUI 输出目录）
EXCLUDE = ("app_data/", "portable.ini")

# Inno Setup 编译器候选位置：优先 PATH，其次 scoop 与常见安装目录
ISCC_CANDIDATES = (
    "iscc",
    r"C:\Home\Develop\Scoop\shims\iscc.exe",
    r"C:\Home\Develop\Scoop\apps\inno-setup\current\iscc.exe",
    r"C:\Program Files (x86)\Inno Setup 6\iscc.exe",
    r"C:\Program Files\Inno Setup 6\iscc.exe",
)


def run(cmd: str):
    """在项目根目录执行 shell 命令，失败即终止。"""
    print(f"-> {cmd}")
    ret = subprocess.run(cmd, shell=True, cwd=str(ROOT), check=False).returncode
    if ret != 0:
        raise SystemExit(f"命令执行失败（exit {ret}）：{cmd}")


def get_version(positional: list[str] | None = None) -> str:
    """优先取位置参数中的版本号，否则解析 Directory.Build.props 中的 Major/Minor/Patch。"""
    if positional:
        v = positional[0]
        if not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+", v):
            raise SystemExit(f"版本号格式应为 x.y.z，收到：{v}")
        return v
    props = (ROOT / "Directory.Build.props").read_text(encoding="utf-8")
    fields = {}
    for name in ("Major", "Minor", "Patch"):
        m = re.search(rf"<{name}>(\d+)</{name}>", props)
        if m is None:
            raise SystemExit(f"无法从 Directory.Build.props 解析 <{name}>")
        fields[name] = m.group(1)
    return "{Major}.{Minor}.{Patch}".format(**fields)


def build():
    """Release 构建两个项目（依赖顺序：GUI 先建，CLI 引用 GUI）。"""
    run("dotnet build src/CarroDesk.csproj -c Release")
    run("dotnet build cli/CarroDesk.Cli/CarroDesk.Cli.csproj -c Release")


def copy_cli_exe():
    """把 CLI 单 exe 复制到 GUI 输出目录，GUI 目录即完整分发包。"""
    src = CLI_OUT / "CarroDesk.Cli.exe"
    if not src.exists():
        raise SystemExit(f"CLI 产物缺失：{src}")
    shutil.copy2(src, GUI_OUT / src.name)
    print(f"-> {GUI_OUT / src.name}")


def zip_gui(release_dir: Path, version: str) -> Path:
    """
    把 GUI 输出目录（CarroDesk.exe + CarroDesk.Cli.exe + 样例配置）打包成 zip。
    GUI/CLI 均为 Costura 嵌入的单 exe，无需额外 DLL；排除 pdb 与运行时生成文件。
    """
    if not (GUI_OUT / "CarroDesk.exe").exists():
        raise SystemExit(f"GUI 构建产物缺失：{GUI_OUT / 'CarroDesk.exe'}")
    dst = release_dir / f"CarroDesk-{version}-windows-x64.zip"
    with zipfile.ZipFile(dst, "w", zipfile.ZIP_DEFLATED) as zf:
        for f in sorted(GUI_OUT.rglob("*")):
            if not f.is_file():
                continue
            rel = f.relative_to(GUI_OUT).as_posix()
            if rel.endswith(".pdb") or rel.startswith(EXCLUDE):
                continue
            zf.write(f, rel)
    size_mb = dst.stat().st_size / 1024 / 1024
    print(f"-> {dst.name}（{size_mb:.1f} MB）")
    return dst


def write_sha256(release_dir: Path):
    """为 Release 目录内所有分发产物（zip / exe）生成 SHA256 校验清单。"""
    dst = release_dir / "SHA256SUMS.txt"
    targets = sorted(list(release_dir.glob("*.zip")) + list(release_dir.glob("*.exe")))
    with open(dst, "w", encoding="utf-8") as out:
        for f in targets:
            digest = hashlib.sha256(f.read_bytes()).hexdigest()
            out.write(f"{digest}  {f.name}\n")
    print(f"-> {dst.name}")
    if not targets:
        print("   （无产物可校验）")


def find_iscc() -> str | None:
    """定位 iscc.exe：优先 PATH，其次常见安装目录。找不到返回 None。"""
    for cand in ISCC_CANDIDATES:
        if os.path.isfile(cand):
            return cand
        which = shutil.which(cand)
        if which:
            return which
    return None


def build_setup(release_dir: Path, version: str) -> Path | None:
    """
    编译 Inno Setup per-user 安装包（免 UAC）。

    安装包在覆盖程序文件前会检测并提示关闭 CarroDesk.exe 与 CarroDesk.Cli.exe，
    支持漫游与便携模式选择、平滑升级与数据保护。未安装 Inno Setup 时给出明确提示并跳过，
    不影响 zip 便携包产出。
    """
    iscc = find_iscc()
    if iscc is None:
        print("!! 未找到 Inno Setup（iscc.exe），跳过安装包生成。")
        print("   安装 Inno Setup 后重试：scoop install inno-setup")
        print("   或显式跳过：python scripts/release.py --no-setup")
        return None

    for required in (GUI_OUT / "CarroDesk.exe", GUI_OUT / "CarroDesk.Cli.exe", APP_ICON, ISS_SCRIPT):
        if not required.exists():
            raise SystemExit(f"构建安装包所需文件缺失：{required}")

    base_name = f"CarroDesk-{version}-setup"
    dst = release_dir / f"{base_name}.exe"
    cmd = (
        f'"{iscc}" /Qp '
        f'/DAppVersion={version} '
        f'"/DPayloadDir={GUI_OUT}" '
        f'"/DIconFile={APP_ICON}" '
        f'"/O{release_dir}" '
        f'"/F{base_name}" '
        f'"{ISS_SCRIPT}"'
    )
    run(cmd)
    if not dst.exists():
        raise SystemExit(f"安装包未生成：{dst}")
    print(f"-> {dst.name}（{dst.stat().st_size / 1024 / 1024:.1f} MB）")
    return dst


def make_release():
    args = [a for a in sys.argv[1:] if a.startswith("--")]
    positional = [a for a in sys.argv[1:] if not a.startswith("--")]

    if "--help" in args or "-h" in args:
        print(USAGE)
        return

    want_zip = "--setup-only" not in args
    want_setup = "--no-setup" not in args

    version = get_version(positional)
    release_dir = (ROOT / "release" / version).resolve()
    release_dir.mkdir(parents=True, exist_ok=True)

    build()
    copy_cli_exe()

    if want_zip:
        zip_gui(release_dir, version)
    if want_setup:
        build_setup(release_dir, version)

    write_sha256(release_dir)

    print(f"\n== Release 产物已就绪：{release_dir} ==")
    for f in sorted(release_dir.iterdir()):
        if f.is_file():
            print(f"  - {f.name}（{f.stat().st_size / 1024 / 1024:.1f} MB）")


if __name__ == "__main__":
    make_release()
