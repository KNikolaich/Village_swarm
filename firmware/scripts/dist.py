# Copies the flashable images of each built env to firmware/dist/<env>/ with a build.json describing
# the chip and flash offsets, for the web flasher (the api serves /api/firmware/<env>/manifest.json, spec 5.3).
import json
import os
import shutil

Import("env")  # noqa: F821 (provided by PlatformIO)


def copy_images(source, target, env):
    build = env.subst("$BUILD_DIR")
    name = env.subst("$PIOENV")
    out = os.path.join(env.subst("$PROJECT_DIR"), "dist", name)
    os.makedirs(out, exist_ok=True)
    mcu = env.BoardConfig().get("build.mcu", "esp32")

    if mcu == "esp8266":
        # One image at 0x0 contains bootloader and app.
        parts = [("firmware.bin", 0x0)]
        family = "ESP8266"
    else:
        framework = env.PioPlatform().get_package_dir("framework-arduinoespressif32")
        shutil.copy2(os.path.join(framework, "tools", "partitions", "boot_app0.bin"), os.path.join(out, "boot_app0.bin"))
        # Classic ESP32 boots from 0x1000, the C3/S3 from 0x0.
        boot = 0x1000 if mcu == "esp32" else 0x0
        parts = [("bootloader.bin", boot), ("partitions.bin", 0x8000), ("boot_app0.bin", 0xE000), ("firmware.bin", 0x10000)]
        family = {"esp32": "ESP32", "esp32c3": "ESP32-C3", "esp32s3": "ESP32-S3"}.get(mcu, mcu.upper())

    for file, _ in parts:
        if file != "boot_app0.bin":
            shutil.copy2(os.path.join(build, file), os.path.join(out, file))
    role, _, board = name.partition("-")
    # HORNET_BOARD from the build flags names the board as people know it (esp-01, wemos-d1-mini...).
    for define in env.get("CPPDEFINES", []):
        if isinstance(define, (list, tuple)) and define[0] == "HORNET_BOARD":
            board = str(define[1]).strip('\\"')
    with open(os.path.join(out, "build.json"), "w", encoding="utf-8") as f:
        json.dump({
            "env": name,
            "role": role if name != "guard-cam" else "guard-cam",
            "board": board,
            "chipFamily": family,
            "parts": [{"file": file, "offset": offset} for file, offset in parts],
        }, f, indent=2)
    print(f"[dist] {family} images copied to {out}")


env.AddPostAction("$BUILD_DIR/${PROGNAME}.bin", copy_images)  # noqa: F821
