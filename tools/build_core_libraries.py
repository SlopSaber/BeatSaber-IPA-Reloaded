"""Build the historical runtime dependency bundle from upstream tags and NuGet.

Only runtime project references are included. Game/Unity binaries, build tools,
test packages and reference assemblies are excluded. Normal builds use the
committed archive and do not need Python, Git history or network access.
"""
import argparse
import collections
import hashlib
import io
import json
import pathlib
import os
import re
import subprocess
import urllib.request
import xml.etree.ElementTree as ET
import zipfile

ROOT = pathlib.Path(__file__).resolve().parent.parent
NS = "{http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd}"
CACHE = ROOT / "CoreLibraries" / "obj"


def git(*args, binary=False):
    result = subprocess.check_output(["git", "-C", str(ROOT), *args])
    return result if binary else result.decode("utf-8", errors="replace")


def framework_rank(name):
    name = name.lower().replace(".netframework", "net").replace(".netstandard", "netstandard").replace("version=v", "").replace("v", "").replace(".", "")
    if name.startswith("netstandard"):
        return {"netstandard10": 110, "netstandard11": 111, "netstandard12": 112, "netstandard13": 113, "netstandard16": 116, "netstandard20": 120}.get(name, -1)
    if re.fullmatch(r"net\d+", name):
        digits = name[3:]
        if len(digits) == 2:
            digits += "0"
        value = int(digits)
        return value if 200 <= value <= 472 else -1
    if name == "":
        return 0
    return -1


def package_data(package, version):
    CACHE.mkdir(parents=True, exist_ok=True)
    path = CACHE / (package.lower() + "." + version.lower() + ".nupkg")
    installed = pathlib.Path(os.environ.get("NUGET_PACKAGES", pathlib.Path.home() / ".nuget/packages")) / package.lower() / version.lower() / path.name
    if not path.exists() and installed.exists():
        path.write_bytes(installed.read_bytes())
    if not path.exists():
        url = f"https://api.nuget.org/v3-flatcontainer/{package.lower()}/{version.lower()}/{package.lower()}.{version.lower()}.nupkg"
        with urllib.request.urlopen(url, timeout=90) as response:
            data = response.read()
        path.write_bytes(data)
    return path.read_bytes()


def bootstrap_libraries():
    """Use the restored runtime asset graph, matching what MSBuild copies."""
    files = list((ROOT / "Libs").glob("*.dll"))
    for project in ("IPA.Loader", "IPA.Injector"):
        graph_path = ROOT / project / "obj/project.assets.json"
        if not graph_path.exists():
            raise ValueError(f"Restore {project} before regenerating the historical bundle")
        graph = json.loads(graph_path.read_text(encoding="utf-8-sig"))
        target = next(value for name, value in graph["targets"].items()
                      if name in ("net472", ".NETFramework,Version=v4.7.2"))
        for package, resolved in target.items():
            library = graph["libraries"][package]
            if library["type"] != "package":
                continue
            for asset in resolved.get("runtime", {}):
                if not asset.lower().endswith(".dll"):
                    continue
                for folder in graph["packageFolders"]:
                    file = pathlib.Path(folder) / library["path"] / asset
                    if file.exists():
                        files.append(file)
                        break
    return {hashlib.sha256(file.read_bytes()).hexdigest(): file.name for file in files}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", default=str(ROOT / "CoreLibraries/HistoricalLibraries.zip"))
    args = parser.parse_args()
    output = pathlib.Path(args.output).resolve()
    output.relative_to(ROOT)
    runtime_projects = ("IPA.Loader/", "IPA.Injector/", "IllusionInjector/", "IllusionPlugin/", "IPA.Old/")
    references = collections.defaultdict(set)
    blobs = {}
    for tag in git("tag", "-l").splitlines():
        if not re.fullmatch(r"[34]\.\d+\.\d+(?:\.\d+)?", tag):
            continue
        for line in git("ls-tree", "-r", tag).splitlines():
            metadata, path = line.split("\t", 1)
            blob = metadata.split()[2]
            if path.startswith("Libs/") and path.endswith(".dll") and not pathlib.PurePosixPath(path).name.startswith("UnityEngine"):
                entry = blobs.setdefault(blob, {"path": path, "releases": set(), "tag": tag})
                entry["releases"].add(tag)
            if path.startswith(runtime_projects) and (path.endswith(".csproj") or path.endswith("packages.config")):
                source = git("show", tag + ":" + path)
                for package, version in re.findall(r'<PackageReference\s+Include="([^"]+)"\s+Version="([^"]+)"', source) + re.findall(r'<package\s+id="([^"]+)"\s+version="([^"]+)"', source):
                    references[(package, version)].add(tag)
    providers, assets = [], {}
    names = set()
    for blob, entry in sorted(blobs.items()):
        data = git("cat-file", "blob", blob, binary=True)
        if data[:2] != b"MZ":
            raise ValueError("A historical library is not a PE binary: " + entry["path"])
        path = "Versions/Upstream/" + blob + "/" + pathlib.PurePosixPath(entry["path"]).name
        assets[path] = data
        names.add(re.sub(r"\.net[34]$", "", pathlib.PurePosixPath(path).stem, flags=re.I))
        providers.append({"path": path, "sha256": hashlib.sha256(data).hexdigest(), "gitBlob": blob, "releases": sorted(entry["releases"]), "source": "https://github.com/nike4613/BeatSaber-IPA-Reloaded/blob/" + entry["tag"] + "/" + entry["path"]})
    # Runtime transitive packages are kept as separate providers as well.
    pending = list(references)
    done = set()
    dependency_graph = collections.defaultdict(set)
    while pending:
        package, version = pending.pop(0)
        if (package, version) in done:
            continue
        done.add((package, version))
        if package.lower().startswith(("xunit", "microsoft.build", "microsoft.visualstudio", "microsoft.netcore.platforms", "microsoft.netcore.targets")):
            continue
        print("Collecting", package, version, flush=True)
        raw = package_data(package, version)
        archive = zipfile.ZipFile(io.BytesIO(raw))
        prefix = f"Versions/NuGet/{package}/{version}/"
        available = {path.split("/")[1] for path in archive.namelist() if path.startswith("lib/") and path.endswith(".dll") and len(path.split("/")) == 3}
        compatible = [tfm for tfm in available if framework_rank(tfm) >= 0]
        tfm = max(compatible, key=framework_rank) if compatible else None
        for path in archive.namelist():
            if (tfm and path.startswith("lib/" + tfm + "/") and path.endswith(".dll") and len(path.split("/")) == 3):
                data = archive.read(path)
                destination = prefix + pathlib.PurePosixPath(path).name
                assets[destination] = data
                names.add(pathlib.PurePosixPath(destination).stem)
                providers.append({"path": destination, "sha256": hashlib.sha256(data).hexdigest(), "package": package, "packageVersion": version, "framework": tfm, "packageSha256": hashlib.sha256(raw).hexdigest(), "releases": sorted(references[(package, version)]), "source": f"https://www.nuget.org/packages/{package}/{version}"})
            if path.lower().endswith(".nuspec") or ("/" not in path and any(word in path.lower() for word in ("license", "notice", "copyright"))):
                assets[prefix + "Licenses/" + pathlib.PurePosixPath(path).name] = archive.read(path)
        nuspec_name = next(path for path in archive.namelist() if path.endswith(".nuspec"))
        spec = ET.fromstring(archive.read(nuspec_name))
        groups = [element for element in spec.iter() if element.tag.split("}")[-1] == "group" and "targetFramework" in element.attrib]
        compatible_groups = [group for group in groups if framework_rank(group.attrib["targetFramework"]) >= 0]
        group = max(compatible_groups, key=lambda g: framework_rank(g.attrib["targetFramework"])) if compatible_groups else None
        dependencies = list(group) if group is not None else [element for element in spec.iter() if element.tag.split("}")[-1] == "dependency"] if not groups else []
        for dependency in dependencies:
            if dependency.tag.split("}")[-1] != "dependency":
                continue
            dependency_name = dependency.attrib["id"]
            match = re.search(r"\d+\.\d+(?:\.\d+){0,2}(?:-[A-Za-z0-9.-]+)?", dependency.attrib.get("version", ""))
            if not match:
                continue
            key = dependency_name, match.group()
            dependency_graph[(package, version)].add(key)
            references[key].update(references[(package, version)])
            if key not in done:
                pending.append(key)
    changed = True
    while changed:
        changed = False
        for parent, children in dependency_graph.items():
            for child in children:
                before = len(references[child])
                references[child].update(references[parent])
                changed |= len(references[child]) != before
    for provider in providers:
        if "package" in provider:
            provider["releases"] = sorted(references[(provider["package"], provider["packageVersion"])])
    source_provider_count = len(providers)
    # Group adjacent releases with identical inventories; each DLL's bytes are
    # stored once, even when other inventories also need that same library.
    source_assets = assets
    assets = {path.replace("Versions/NuGet/", "Licenses/").replace("/Licenses/", "/"): data
              for path, data in source_assets.items() if path not in {p["path"] for p in providers}}
    by_release = collections.defaultdict(list)
    for provider in providers:
        for release in provider["releases"]:
            by_release[release].append(provider)
    def release_key(release):
        return tuple(int(n) for n in release.split("."))
    groups = []
    for release in sorted(by_release, key=release_key):
        signature = frozenset((pathlib.PurePosixPath(p["path"]).name, p["sha256"]) for p in by_release[release])
        if groups and groups[-1]["signature"] == signature:
            groups[-1]["releases"].append(release)
        else:
            groups.append({"signature": signature, "releases": [release]})
    stored = {}
    bootstrap = bootstrap_libraries()
    shared_bootstrap = {}
    release_groups = []
    for group in groups:
        releases = group["releases"]
        label = releases[0] if len(releases) == 1 else releases[0] + "-" + releases[-1]
        directory = "Versions/" + label
        paths = set()
        for provider in sorted(by_release[releases[0]], key=lambda p: p["path"]):
            filename = pathlib.PurePosixPath(provider["path"]).name
            digest = provider["sha256"]
            if digest not in stored:
                if digest in bootstrap:
                    destination = bootstrap[digest]
                    shared_bootstrap[destination] = digest
                else:
                    destination = f"{directory}/{filename}"
                    if destination in assets:
                        destination = f"{directory}/{pathlib.PurePosixPath(filename).stem}.{digest[:8]}.dll"
                    assets[destination] = source_assets[provider["path"]]
                stored[digest] = dict(provider, path=destination, releases=[])
            record = stored[digest]
            record["releases"] = sorted(set(record["releases"]) | set(releases), key=release_key)
            paths.add(record["path"])
        mapping = {"directory": directory, "releases": releases, "files": sorted(paths)}
        release_groups.append(mapping)
        assets[directory + "/libraries.json"] = (json.dumps(mapping, indent=2) + "\n").encode()
    # Preserve all package/source provenance, including providers sharing bytes.
    for digest, record in stored.items():
        record["origins"] = [p for p in providers if p["sha256"] == digest]
    providers = sorted(stored.values(), key=lambda p: p["path"])
    assets["CoreLibraryIndex.json"] = (json.dumps({"format": 3, "uniqueProviders": len(providers), "sourceProviders": source_provider_count, "releaseGroups": release_groups, "coreAssemblyNames": sorted(names), "providers": providers}, indent=2) + "\n").encode()
    assets["provider-releases.tsv"] = "".join(provider["path"] + "\t" + ",".join(provider["releases"]) + "\n" for provider in providers).encode()
    license_source = git("show", "4.3.7:LICENSE")
    assets["Historical-LICENSE.txt"] = license_source.encode()
    # The committed archive and distributable both retain one copy per binary.
    packed = {}
    project = ET.Element("Project")
    items = ET.SubElement(project, "ItemGroup")
    for path, data in sorted(assets.items()):
        source = "Objects/" + hashlib.sha256(data).hexdigest() + ".dll" if path.endswith(".dll") else path
        packed[source] = data
        item = ET.SubElement(items, "HistoricalLibrary", Include=path)
        ET.SubElement(item, "Source").text = source
        ET.SubElement(item, "TargetPath").text = "IPA/CoreLibraries/" + path
    for path, digest in sorted(shared_bootstrap.items()):
        item = ET.SubElement(items, "BootstrapLibrary", Include=path)
        ET.SubElement(item, "ExpectedHash").text = digest.upper()
    ET.indent(project)
    props_path = output.with_suffix(".props")
    ET.ElementTree(project).write(props_path, encoding="utf-8", xml_declaration=True)
    output.parent.mkdir(parents=True, exist_ok=True)
    temporary = output.with_suffix(".tmp")
    with zipfile.ZipFile(temporary, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for path, data in sorted(packed.items()):
            info = zipfile.ZipInfo(path, (2026, 10, 14, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            archive.writestr(info, data)
    temporary.replace(output)
    output.with_suffix(output.suffix + ".sha256").write_text(hashlib.sha256(output.read_bytes()).hexdigest().upper() + "\n")
    print(json.dumps({"providers": len(providers), "sharedBootstrap": len(shared_bootstrap), "releaseGroups": len(groups), "assemblies": len(names), "bytes": output.stat().st_size}), flush=True)


if __name__ == "__main__":
    main()
