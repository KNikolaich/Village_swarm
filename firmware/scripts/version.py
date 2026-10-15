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


# Signed over-the-air updates (docs/ota.md): the public key in ota_public_key.hex is built into the firmware, which then
# only accepts images signed with the matching private key. No key file, or HORNET_ALLOW_UNSIGNED=1 in the environment,
# makes a development build that also accepts unsigned images: never put such a build on a hornet that stays away.
def ota_policy():
    import os
    key_file = os.path.join(env.subst("$PROJECT_DIR"), "ota_public_key.hex")  # noqa: F821
    key = open(key_file, encoding="ascii").read().strip() if os.path.exists(key_file) else ""
    allow = os.environ.get("HORNET_ALLOW_UNSIGNED") == "1"
    if key:
        env.Append(CPPDEFINES=[("HORNET_OTA_PUBKEY", env.StringifyMacro(key))])  # noqa: F821
    elif not allow:
        print("[ota] WARNING: no ota_public_key.hex: this build accepts unsigned updates (run scripts/ota_sign.py gen)")
        allow = True
    if allow:
        env.Append(CPPDEFINES=["HORNET_OTA_ALLOW_UNSIGNED"])  # noqa: F821
    print("[ota] updates: " + ("signed only" if key and not allow else "unsigned images accepted"))


ota_policy()

