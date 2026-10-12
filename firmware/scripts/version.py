# Defines HORNET_FW_VERSION = <release>+<git commit>[-dirty], so every build has its own version string.
# The hive compares it with what a hornet reports (info.fw) to know an over-the-air update took.
import subprocess

Import("env")  # noqa: F821 (provided by PlatformIO)

RELEASE = "0.2.0"


def commit():
    try:
        out = subprocess.check_output(
            ["git", "describe", "--always", "--dirty", "--abbrev=7", "--match", "never-matches"],
            cwd=env.subst("$PROJECT_DIR"), text=True, stderr=subprocess.DEVNULL,
        ).strip()
        return out
    except Exception:
        return "nogit"


env.Append(CPPDEFINES=[("HORNET_FW_VERSION", env.StringifyMacro(f"{RELEASE}+{commit()}"))])  # noqa: F821
