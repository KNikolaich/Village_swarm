"""Checks scripts/fw_stale.py on a copy of the firmware tree: python scripts/test_fw_stale.py"""
import os
import shutil
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
os.environ["HORNET_OTA_KEY"] = os.path.join(tempfile.gettempdir(), "no-such-signing-key")  # same answer on every machine
import fw_stale  # noqa: E402

root = tempfile.mkdtemp()
try:
    for name in ("lib", "src", "scripts"):
        shutil.copytree(os.path.join(os.path.dirname(HERE), name), os.path.join(root, name), ignore=shutil.ignore_patterns("__pycache__"))
    shutil.copy(os.path.join(os.path.dirname(HERE), "platformio.ini"), root)
    fw_stale.PROJECT = root
    cp = fw_stale.read_config(os.path.join(root, "platformio.ini"))
    all_envs = fw_stale.envs(cp)
    assert "native" not in all_envs and "meteo-c3" in all_envs

    def stale_set():
        return {e for e in all_envs if fw_stale.stale(cp, e)}

    def build_all():
        for e in all_envs:
            out = fw_stale.dist_dir(e)
            os.makedirs(out, exist_ok=True)
            for f in fw_stale.IMAGES:
                open(os.path.join(out, f), "w").close()
            with open(os.path.join(out, ".inputs"), "w") as f:
                f.write(fw_stale.fingerprint(cp, e) + "\n")

    def touch(rel, text="\n// changed"):
        with open(os.path.join(root, rel), "a") as f:
            f.write(text)

    assert stale_set() == set(all_envs), "nothing built yet: all stale"
    build_all()
    assert stale_set() == set(), "nothing changed: nothing to build"

    touch("src/roles/relay.cpp")
    assert stale_set() == {e for e in all_envs if e.startswith("relay-")}, "a role file: only the envs that build it"
    build_all()

    touch("src/roles/heat.cpp")
    assert stale_set() == {e for e in all_envs if e.startswith("heat-")}
    build_all()

    touch("src/main.cpp")
    assert stale_set() == set(all_envs), "shared source: every env"
    build_all()

    # One board's settings: only the envs that inherit them.
    with open(os.path.join(root, "platformio.ini")) as f:
        ini = f.read()
    cp_text = ini.replace("[c3_base]\n", "[c3_base]\nmonitor_filters = default\n", 1)
    assert cp_text != ini
    with open(os.path.join(root, "platformio.ini"), "w") as f:
        f.write(cp_text)
    cp = fw_stale.read_config(os.path.join(root, "platformio.ini"))
    changed = stale_set()
    assert changed and all("c3" in e for e in changed) and "meteo-c3" in changed and "meteo-d1mini" not in changed, changed
    build_all()

    # A missing image is stale however the inputs look.
    os.remove(os.path.join(fw_stale.dist_dir("guard-cam"), "firmware.bin"))
    assert stale_set() == {"guard-cam"}
    print("fw_stale: ok")
finally:
    shutil.rmtree(root, ignore_errors=True)
