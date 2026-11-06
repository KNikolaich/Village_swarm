"""Tells deploy.bat which hornet firmwares need a rebuild, so the unchanged ones are neither rebuilt nor re-signed.

    python scripts/fw_stale.py list [--all]   envs that need a build, one per line (--all: every env, for a forced build)
    python scripts/fw_stale.py stamp <env>    remember the inputs of <env> after it built (firmware/dist/<env>/.inputs)

An env needs a build when firmware/dist/<env> lacks the images, or when the fingerprint of its inputs differs from the
one stamped at its last build. The fingerprint covers what scripts/version.py hashes into the firmware version (the
shared library, main.cpp, the role file this env builds, the env's resolved settings, the OTA public key) plus the
things that change the build without changing the version: RELEASE in version.py, and whether a signing key exists.
Any doubt (a setting this parser cannot read) means "build": PlatformIO decides, the version hash stays the authority.
Python 3 standard library only, so it runs without PlatformIO.
"""
import configparser
import hashlib
import os
import re
import sys

PROJECT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SKIP_ENVS = {"native"}  # unit tests on the PC, no firmware
IMAGES = ("firmware.bin", "build.json")


def read_config(path):
    cp = configparser.RawConfigParser(interpolation=None, strict=False)
    cp.optionxform = str
    with open(path, encoding="utf-8") as f:
        cp.read_file(f)
    return cp


def resolve(cp, section, _seen=()):
    """The options of a section with 'extends' applied (parents first, [env] under every env:*) and ${section.key} expanded."""
    if section in _seen:
        raise ValueError("extends loop at " + section)
    merged = {}
    if section.startswith("env:") and cp.has_section("env"):
        merged.update(cp.items("env"))
    for parent in re.split(r"[,\s]+", cp.get(section, "extends", fallback="").strip()):
        if parent:
            merged.update(resolve(cp, parent if cp.has_section(parent) else "env:" + parent, _seen + (section,)))
    merged.update(cp.items(section))
    merged.pop("extends", None)
    return merged


def expand(cp, value, depth=0):
    if depth > 10:
        raise ValueError("${} nesting too deep")

    def sub(m):
        sec, key = m.group(1), m.group(2)
        # ${env.key} is the value in the shared [env] section, as PlatformIO reads it.
        values = dict(cp.items("env")) if sec == "env" else resolve(cp, sec)
        return expand(cp, values[key], depth + 1)

    return re.sub(r"\$\{([\w:.\-]+)\.([\w.\-]+)\}", sub, value)


def env_options(cp, env):
    section = "env:" + env
    options = resolve(cp, section)
    return {key: expand(cp, value) for key, value in options.items()}


def role_files(options):
    return set(re.findall(r"\+<roles/([\w.\-]+)>", options.get("build_src_filter", "")))


def fingerprint(cp, env):
    h = hashlib.sha1()

    def feed(label, data):
        h.update(label.encode("utf-8") + b"\x00" + data + b"\x00")

    def feed_file(path):
        with open(path, "rb") as f:
            feed(os.path.relpath(path, PROJECT).replace("\\", "/"), f.read().replace(b"\r\n", b"\n"))

    options = env_options(cp, env)
    roles = role_files(options)
    for base in ("lib", "src"):
        for root, dirs, files in os.walk(os.path.join(PROJECT, base)):
            dirs.sort()
            for name in sorted(files):
                rel = os.path.relpath(os.path.join(root, name), PROJECT).replace("\\", "/")
                if rel.startswith("src/roles/") and name not in roles:
                    continue  # another role's file is not part of this build
                feed_file(os.path.join(root, name))
    for key, value in sorted(options.items()):
        if key != "extra_scripts":
            feed("cfg:" + key, value.encode("utf-8"))
    for extra in ("ota_public_key.hex", os.path.join("scripts", "version.py")):  # version.py: RELEASE
        if os.path.exists(os.path.join(PROJECT, extra)):
            feed_file(os.path.join(PROJECT, extra))
    sys.path.insert(0, os.path.join(PROJECT, "scripts"))
    import ota_sign

    # A build made before the signing key existed is unsigned: the same version, but hornets that require signatures refuse it.
    feed("signed", b"1" if ota_sign.load_secret(required=False) is not None else b"0")
    feed("unsigned-ok", os.environ.get("HORNET_ALLOW_UNSIGNED", "").encode("utf-8"))
    return h.hexdigest()


def envs(cp):
    return [s[4:] for s in cp.sections() if s.startswith("env:") and s[4:] not in SKIP_ENVS]


def dist_dir(env):
    return os.path.join(PROJECT, "dist", env)


def stale(cp, env):
    out = dist_dir(env)
    if not all(os.path.exists(os.path.join(out, f)) for f in IMAGES):
        return True
    try:
        with open(os.path.join(out, ".inputs"), encoding="ascii") as f:
            return f.read().strip() != fingerprint(cp, env)
    except (OSError, ValueError, KeyError):
        return True


def main(argv):
    cp = read_config(os.path.join(PROJECT, "platformio.ini"))
    if len(argv) >= 2 and argv[1] == "list":
        force = "--all" in argv[2:]
        for env in envs(cp):
            if force or stale(cp, env):
                print(env)
        return 0
    if len(argv) == 3 and argv[1] == "stamp":
        os.makedirs(dist_dir(argv[2]), exist_ok=True)
        with open(os.path.join(dist_dir(argv[2]), ".inputs"), "w", encoding="ascii") as f:
            f.write(fingerprint(cp, argv[2]) + "\n")
        return 0
    print(__doc__, file=sys.stderr)
    return 2


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
