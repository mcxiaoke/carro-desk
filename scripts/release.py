#
# CarroDesk Release 打包脚本
#
# 流程：
#   1. 从 Directory.Build.props 解析 Major.Minor.Patch 版本号；
#   2. dotnet Release 构建 GUI（src/CarroDesk）与 CLI（cli/CarroDesk.Cli）；
#   3. 把 CLI 单 exe 复制进 GUI 输出目录（两者同时发布，GUI 目录即完整分发包）；
#   4. 打包 zip 到 release/<version>/CarroDesk-<version>-windows-x64.zip；
#   5. 编译 NSIS 安装包到 release/<version>/CarroDesk-<version>-setup.exe（per-user，免 UAC）；
#   6. 生成 SHA256SUMS.txt 校验清单。
#

USAGE = """用法：
  python scripts/release.py                     # 用 Directory.Build.props 里的版本号
  python scripts/release.py 1.2.0               # 指定版本号
  python scripts/release.py --no-setup           # 跳过安装包（未装 NSIS 时可用）
  python scripts/release.py --setup-only        # 只出安装包，跳过 zip
  python scripts/release.py --help              # 显示本帮助

产物（release/<version>/）：
  CarroDesk-<version>-windows-x64.zip          便携版压缩包
  CarroDesk-<version>-setup.exe                NSIS 安装包（per-user，免 UAC）
  SHA256SUMS.txt                               校验清单

安装包特性：
  * 默认安装到 %LOCALAPPDATA%\\Programs\\CarroDesk，无需管理员权限
  * 安装/升级/卸载前自动结束 CarroDesk.exe 与 CarroDesk.Cli.exe（含子进程树）
  * 安装时可选数据模式：漫游（%AppData%\\CarroDesk）或便携（安装目录 app_data）
  * 升级只覆盖程序文件，配置与任务数据保持不变

依赖：NSIS（scoop install nsis）。未安装时自动跳过安装包，zip 不受影响。
"""

import hashlib
import os
import re
import shutil
import subprocess
import sys
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
GUI_PROJECT = ROOT / "src" / "CarroDesk.csproj"
CLI_PROJECT = ROOT / "cli" / "CarroDesk.Cli" / "CarroDesk.Cli.csproj"
GUI_OUT = ROOT / "src" / "bin" / "Release" / "net48"
CLI_OUT = ROOT / "cli" / "CarroDesk.Cli" / "bin" / "Release" / "net48"
NSIS_SCRIPT = ROOT / "scripts" / "installer" / "CarroDesk.nsi"
APP_ICON = ROOT / "src" / "Assets" / "Icon.ico"

# 运行时生成、不应进入发布包的文件/目录（相对 GUI 输出目录）
EXCLUDE = ("app_data/", "portable.ini")

# NSIS 编译器候选位置：优先 PATH，其次 scoop 安装目录
MAKENSIS_CANDIDATES = (
    "makensis",
    r"C:\Home\Develop\Scoop\apps\nsis\current\makensis.exe",
    r"C:\Program Files (x86)\NSIS\makensis.exe",
    r"C:\Program Files\NSIS\makensis.exe",
)


def run(cmd: str):
    """在项目根目录执行 shell 命令，失败即终止。"""
    print(f"-> {cmd}")
    ret = subprocess.run(cmd, shell=True, cwd=str(ROOT), check=False).returncode
    if ret != 0:
        raise SystemExit(f"命令执行失败（exit {ret}）：{cmd}")


def get_version() -> str:
    """优先取命令行参数，否则解析 Directory.Build.props 中的 Major/Minor/Patch。"""
    if len(sys.argv) > 1:
        v = sys.argv[1]
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


def ensure_utf8_bom(path: Path) -> None:
    """
    makensis 以系统 ANSI 代码页读取 .nsi 脚本，中文注释/字符串必须带 UTF-8 BOM，
    否则报 "Bad text encoding"。这里做一次幂等修正，防止编辑器保存时丢 BOM。
    """
    raw = path.read_bytes()
    if not raw.startswith(b"\xef\xbb\xbf"):
        path.write_bytes(b"\xef\xbb\xbf" + raw)
        print(f"-> 已为 {path.name} 补上 UTF-8 BOM")


def find_makensis() -> str | None:
    """定位 makensis.exe：优先 PATH，其次常见安装目录。找不到返回 None。"""
    for cand in MAKENSIS_CANDIDATES:
        if os.path.isfile(cand):
            return cand
        if shutil.which(cand):
            return cand
    return None


def build_setup(release_dir: Path, version: str) -> Path | None:
    """
    编译 NSIS per-user 安装包（免 UAC）。

    安装包在覆盖程序文件前会 taskkill 掉 CarroDesk.exe 与 CarroDesk.Cli.exe，
    因此升级时无需再手动结束进程。未安装 NSIS 时给出明确提示并跳过，
    不影响 zip 便携包产出。
    """
    makensis = find_makensis()
    if makensis is None:
        print("!! 未找到 NSIS（makensis.exe），跳过安装包生成。")
        print("   安装 NSIS 后重试：scoop install nsis")
        print("   或显式跳过：python scripts/release.py --no-setup")
        return None

    for required in (GUI_OUT / "CarroDesk.exe", GUI_OUT / "CarroDesk.Cli.exe", APP_ICON, NSIS_SCRIPT):
        if not required.exists():
            raise SystemExit(f"构建安装包所需文件缺失：{required}")

    ensure_utf8_bom(NSIS_SCRIPT)

    dst = release_dir / f"CarroDesk-{version}-setup.exe"
    cmd = (
        f'"{makensis}" -V2 '
        f'/DVERSION={version} '
        f'"/DPAYLOAD_DIR={GUI_OUT}" '
        f'"/DICON_FILE={APP_ICON}" '
        f'"/DOUTPUT_FILE={dst}" '
        f'"{NSIS_SCRIPT}"'
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

    # get_version 读取 sys.argv[1]，此处按位置参数传入
    if positional:
        sys.argv = [sys.argv[0]] + positional + args

    version = get_version()
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
