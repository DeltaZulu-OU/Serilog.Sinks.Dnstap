#!/usr/bin/env python3
"""Validate a NuGet package's identity, runtime assembly and dependency contract."""
import os
import pathlib
import sys
import xml.etree.ElementTree as ET
import zipfile

if len(sys.argv) != 2:
    raise SystemExit("Usage: verify-nupkg.py PACKAGE.nupkg")

path = pathlib.Path(sys.argv[1])
props = pathlib.Path(__file__).resolve().parents[2] / "Directory.Build.props"
versions = [e.text.strip() for e in ET.parse(props).iter("Version") if e.text and e.text.strip()]
if len(versions) != 1:
    raise SystemExit("Expected one Version in Directory.Build.props")
version = versions[0]
expected = f"Serilog.Sinks.Dnstap.{version}.nupkg"
if path.name != expected:
    raise SystemExit(f"Expected {expected}, got {path.name}")

with zipfile.ZipFile(path) as package:
    names = set(package.namelist())
    for member in ("Serilog.Sinks.Dnstap.nuspec", "lib/net10.0/Serilog.Sinks.Dnstap.dll", "README.md"):
        if member not in names:
            raise SystemExit(f"Missing package entry: {member}")
    nuspec = ET.fromstring(package.read("Serilog.Sinks.Dnstap.nuspec"))

def elements(name):
    return [e for e in nuspec.iter() if e.tag.rsplit("}", 1)[-1] == name]

def single(name):
    found = elements(name)
    if len(found) != 1:
        raise SystemExit(f"Expected one {name}, got {len(found)}")
    return found[0]

for name, expected_value in [
    ("id", "Serilog.Sinks.Dnstap"),
    ("version", version),
    ("readme", "README.md")
]:
    if single(name).text != expected_value:
        raise SystemExit(f"Invalid {name}")

lic = single("license")
if lic.attrib.get("type") != "expression" or lic.text != "Apache-2.0":
    raise SystemExit("Unexpected package license")

repository = single("repository")
if repository.attrib.get("type") != "git":
    raise SystemExit("Missing git repository metadata")
if os.environ.get("GITHUB_SHA") and repository.attrib.get("commit") != os.environ["GITHUB_SHA"]:
    raise SystemExit("Package repository commit does not match tested source")

deps = elements("dependency")
for name, version in [("Serilog", "4.4.0"), ("Dnstap.NET", "0.2.0")]:
    matching = [e for e in deps if e.attrib.get("id") == name]
    if len(matching) != 1 or version not in matching[0].attrib.get("version", ""):
        raise SystemExit(f"Missing or unexpected {name} dependency")

print(f"Validated {path.name} archive and dependency metadata")
