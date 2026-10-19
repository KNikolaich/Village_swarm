# Defines HORNET_FW_VERSION = <release>+<hash of what goes into this build>.
# The hash covers the sources this env compiles (the shared library, main.cpp and only the role file it builds), its
# settings (board, build flags, libraries) and the public key: a change to one role changes the version of the envs
# that build that role and nothing else. So after an update of one hornet type the others do not show a new firmware.
# The hive compares the version with what a hornet reports (info.fw) to know an over-the-air update took.
import hashlib
import os
import re

Import("env")  # noqa: F821 (provided by PlatformIO)

RELEASE = "0.2.1"


def content_hash():
    project = env.subst("$PROJECT_DIR")
    h = hashlib.sha1()

    def feed(label, data):
        h.update(label.encode("utf-8") + b"\x00")
        h.update(data)
        h.update(b"\x00")

    def feed_file(path):
        with open(path, "rb") as f:
            feed(os.path.relpath(path, project).replace("\\", "/"), f.read().replace(b"\r\n", b"\n"))

    src_filter = env.GetProjectOption("build_src_filter") or ""
    if isinstance(src_filter, (list, tuple)):
        src_filter = " ".join(src_filter)
    roles = set(re.findall(r"\+<roles/([\w.\-]+)>", src_filter))
    for base in ("lib", "src"):
        for root, dirs, files in os.walk(os.path.join(project, base)):
            dirs.sort()
            for name in sorted(files):
                path = os.path.join(root, name)
                rel = os.path.relpath(path, project).replace("\\", "/")
                if rel.startswith("src/roles/") and name not in roles:
                    continue  # another role's file is not part of this build
                feed_file(path)
    # The env's own settings, with inheritance resolved (extends): board, flags, libraries. Not the scripts.
    for key, value in sorted(env.GetProjectConfig().items(env=env["PIOENV"])):
        if key != "extra_scripts":
            feed("cfg:" + key, str(value).encode("utf-8"))
    key_file = os.path.join(project, "ota_public_key.hex")
    if os.path.exists(key_file):
        feed_file(key_file)
    return h.hexdigest()[:7]


env.Append(CPPDEFINES=[("HORNET_FW_VERSION", env.StringifyMacro(f"{RELEASE}+{content_hash()}"))])  # noqa: F821


# Signed over-the-air updates (docs/ota.md): the public key in ota_public_key.hex is built into the firmware, which then
# only accepts images signed with the matching private key. No key file, or HORNET_ALLOW_UNSIGNED=1 in the environment,
# makes a development build that also accepts unsigned images: never put such a build on a hornet that stays away.
def ota_policy():
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
