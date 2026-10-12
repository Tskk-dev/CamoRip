"""Choose an available iPhone simulator, creating the requested model if necessary."""

import argparse
import json
import subprocess


def simctl(*args):
    return subprocess.check_output(["xcrun", "simctl", *args], text=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--minimum-ios", type=int, required=True)
    parser.add_argument("--model", default="any")
    args = parser.parse_args()
    runtimes = json.loads(simctl("list", "runtimes", "-j"))["runtimes"]
    compatible = [r for r in runtimes if r.get("isAvailable")
                  and "SimRuntime.iOS-" in r["identifier"]
                  and int(r["version"].split(".")[0]) >= args.minimum_ios]
    compatible.sort(key=lambda r: tuple(map(int, r["version"].split("."))), reverse=True)
    devices = json.loads(simctl("list", "devices", "available", "-j"))["devices"]
    for runtime in compatible:
        for device in devices.get(runtime["identifier"], []):
            if device["name"].startswith("iPhone") and (
                    args.model == "any" or device["name"] == args.model):
                print(device["udid"])
                return
    if args.model != "any" and compatible:
        device_type = "com.apple.CoreSimulator.SimDeviceType." + args.model.replace(" ", "-")
        print(simctl("create", args.model, device_type, compatible[0]["identifier"]).strip())
        return
    raise SystemExit("No compatible iPhone simulator runtime is installed")


if __name__ == "__main__":
    main()
