#
# CarroDesk Release 打包脚本
#
# 流程：
#   1. 从 Directory.Build.props 解析 Major.Minor.Patch 版本号；
#   2. dotnet Release 构建 GUI（src/CarroDesk）与 CLI（cli/CarroDesk.Cli）；
#   3. 把 CLI 单 exe 复制进 GUI 输出目录（两者同时发布，GUI 目录即完整分发包）；
#   4. 打包 zip 到 release/<version>/CarroDesk-<version>-windows-x64.zip，
#      并生成 SHA256SUMS.txt 校验清单。
#
# 用法：python scripts/release.py   （或 python scripts/release.py 1.2.0 指定版本）
#

import hashlib
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

# 运行时生成、不应进入发布包的文件/目录（相对 GUI 输出目录）
EXCLUDE = ("app_data/", "portable.ini")


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
    """为 Release 目录内所有 zip 生成 SHA256 校验清单。"""
    dst = release_dir / "SHA256SUMS.txt"
    with open(dst, "w", encoding="utf-8") as out:
        for f in sorted(release_dir.glob("*.zip")):
            digest = hashlib.sha256(f.read_bytes()).hexdigest()
            out.write(f"{digest}  {f.name}\n")
    print(f"-> {dst.name}")


def make_release():
    version = get_version()
    release_dir = (ROOT / "release" / version).resolve()
    release_dir.mkdir(parents=True, exist_ok=True)

    build()
    copy_cli_exe()
    zip_gui(release_dir, version)
    write_sha256(release_dir)

    print(f"\n== Release 产物已就绪：{release_dir} ==")
    for f in sorted(release_dir.iterdir()):
        if f.is_file():
            print(f"  - {f.name}（{f.stat().st_size / 1024 / 1024:.1f} MB）")


if __name__ == "__main__":
    make_release()
