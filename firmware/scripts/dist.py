# Copies the flashable images of each built env to firmware/dist/<role>/ for the web flasher
# (served by the api at /api/firmware/<role>/manifest.json, spec 5.3).
import os
import shutil

Import("env")  # noqa: F821 (provided by PlatformIO)


def copy_images(source, target, env):
    build = env.subst("$BUILD_DIR")
    role = env.subst("$PIOENV")
    out = os.path.join(env.subst("$PROJECT_DIR"), "dist", role)
    os.makedirs(out, exist_ok=True)
    for name in ("bootloader.bin", "partitions.bin", "firmware.bin"):
        shutil.copy2(os.path.join(build, name), os.path.join(out, name))
    framework = env.PioPlatform().get_package_dir("framework-arduinoespressif32")
    shutil.copy2(os.path.join(framework, "tools", "partitions", "boot_app0.bin"), os.path.join(out, "boot_app0.bin"))
    print(f"[dist] images copied to {out}")


env.AddPostAction("$BUILD_DIR/${PROGNAME}.bin", copy_images)  # noqa: F821
