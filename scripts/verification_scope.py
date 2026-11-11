"""Conservative diff classification shared by local receipts and CI selection."""

import hashlib
import io
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
TEMPLATE_TOOLS = {"scripts/create_feature.py", "scripts/check_feature_template.py",
                  "scripts/tests/test_create_feature.py", "scripts/tests/test_check_feature_template.py"}
XML_SUFFIXES = {".csproj", ".fsproj", ".props", ".targets", ".slnx"}


def git_bytes(root, *args):
    return subprocess.check_output(["git", *args], cwd=root)


def revision(root, ref):
    return git_bytes(root, "rev-parse", "--verify", ref).decode().strip()


def committed_tree(root, ref):
    result, blobs = {}, []
    for entry in git_bytes(root, "ls-tree", "-rz", ref).split(b"\0"):
        if entry:
            metadata, name = entry.split(b"\t", 1)
            mode, kind, identity = metadata.decode().split()
            if kind == "blob":
                blobs.append((name.decode(), mode, identity))
            else:
                result[name.decode()] = (mode, b"submodule:" + identity.encode())
    if blobs:
        raw = subprocess.check_output(["git", "cat-file", "--batch"], cwd=root,
                                      input="".join(item[2] + "\n" for item in blobs).encode())
        offset = 0
        for name, mode, identity in blobs:
            end = raw.index(b"\n", offset)
            header = raw[offset:end].decode().split()
            if header[:2] != [identity, "blob"]:
                raise ValueError("Invalid Git blob inventory")
            size = int(header[2])
            result[name] = (mode, raw[end + 1:end + 1 + size])
            offset = end + size + 2
    return result


def working_tree(root):
    names = git_bytes(root, "ls-files", "-z", "--cached", "--others", "--exclude-standard").split(b"\0")
    result = {}
    for raw in set(names) - {b""}:
        name = raw.decode()
        path = root / name
        if path.is_symlink():
            result[name] = ("120000", os.readlink(path).encode())
        elif path.is_file():
            result[name] = ("100755" if path.stat().st_mode & 0o111 else "100644", path.read_bytes())
    return result


def fingerprint(tree):
    digest = hashlib.sha256()
    for name, (mode, data) in sorted(tree.items()):
        digest.update(name.encode() + b"\0" + mode.encode() + b"\0" + data + b"\0")
    return digest.hexdigest()


def documentation(name):
    path = Path(name)
    if any(part.casefold() in {"fixtures", "testdata", "snapshots", "__snapshots__"}
           or ".tests" in part.casefold() for part in path.parts):
        return False
    if name == "templates/feature/README.md.tpl":
        return True
    if path.suffix != ".md":
        return False
    module_readme = path.name == "README.md" and name.startswith(
        ("src/", "infrastructure/", "tests/", "examples/", ".github/"))
    return (name in {"AGENTS.md", "README.md", "templates/feature/README.md"}
            or module_readme or name.startswith(("docs/", ".codex/skills/"))
            or (name.startswith("src/features/") and len(path.parts) >= 4 and path.parts[3] == "docs"))


def xml_shape(data):
    text = data.decode("utf-8-sig")
    if "\0" in text:
        raise ValueError("Only UTF-8 XML has a supported formatting proof")
    if any(marker in data for marker in (b"<!DOCTYPE", b"<![CDATA[", b"xml:space")):
        raise ValueError("Whitespace-sensitive XML requires normal verification")
    parser = ET.XMLParser(target=ET.TreeBuilder(insert_comments=True, insert_pis=True))
    root = ET.fromstring(data, parser=parser)
    local = lambda node: node.tag.rsplit("}", 1)[-1] if isinstance(node.tag, str) else ""
    if local(root) not in {"Project", "Solution"}:
        raise ValueError("Unknown XML document requires normal verification")

    def validate(node, parent=None, grandparent=None):
        tag, owner, group = local(node), local(parent) if parent is not None else "", local(grandparent) if grandparent is not None else ""
        scalar = owner == "PropertyGroup" or group in {"ItemGroup", "ItemDefinitionGroup"} or tag == "Property"
        opaque = tag == "ProjectExtensions" or (tag == "Task" and owner == "UsingTask")
        if len(node) and (scalar or opaque):
            raise ValueError("Nested XML string/task payload requires normal verification")
        if "{http://www.w3.org/XML/1998/namespace}space" in node.attrib:
            raise ValueError("Whitespace-sensitive XML requires normal verification")
        for child in node:
            validate(child, node, parent)

    validate(root)
    namespaces = [binding for _, binding in ET.iterparse(io.BytesIO(data), events=("start-ns",))]

    def shape(node):
        text = node.text or ""
        if len(node) and text.isspace():
            text = ""
        return (str(node.tag), sorted(node.attrib.items()), text,
                [(shape(child), "" if (child.tail or "").isspace() else child.tail or "") for child in node])
    return (re.findall(rb"<!--.*?-->|<\?.*?\?>", data, re.S), namespaces, shape(root))


def simple_format(name, before, after):
    suffix = Path(name.removesuffix(".tpl")).suffix
    try:
        if suffix in XML_SUFFIXES:
            return xml_shape(before) == xml_shape(after)
        if suffix == ".json":
            # Preserve property order and lexical literals; reject non-JSON constants.
            json.loads(before, parse_constant=lambda _: (_ for _ in ()).throw(ValueError()))
            json.loads(after, parse_constant=lambda _: (_ for _ in ()).throw(ValueError()))
            tokens = lambda data: re.findall(rb'"(?:[^"\\]|\\.)*"|[^\s]', data)
            return tokens(before) == tokens(after)
    except (ValueError, ET.ParseError, UnicodeError):
        pass
    return False


def csharp_equivalent(pairs, root=ROOT):
    if not pairs:
        return []
    try:
        decoded = [{"Before": a.decode("utf-8-sig"), "After": b.decode("utf-8-sig")} for a, b in pairs]
    except UnicodeError:
        return [False] * len(pairs)
    output = root / ".artifacts/change-proof"
    output.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(dir=output) as temporary:
        request = Path(temporary) / "input.json"
        request.write_text(json.dumps(decoded))
        log = output / "build.log"
        project = ROOT / "scripts/ChangeProof/ChangeProof.csproj"
        with log.open("w") as stream:
            subprocess.run(["dotnet", "build", str(project), "--nologo", "--disable-build-servers",
                            "-nodeReuse:false", "-maxcpucount:2", "-verbosity:quiet"],
                           cwd=ROOT, stdout=stream, stderr=subprocess.STDOUT, check=True)
        raw = subprocess.check_output(["dotnet", str(project.parent / "bin/Debug/net11.0/ChangeProof.dll"),
                                       str(request)], cwd=ROOT)
        result = json.loads(raw)
        if len(result) != len(pairs) or any(type(value) is not bool for value in result):
            raise ValueError("Invalid C# formatting proof result")
        return result


def classify_trees(before, after, proof=csharp_equivalent):
    entries, pending, pairs = [], [], []
    for name in sorted(before.keys() | after.keys()):
        old, new = before.get(name), after.get(name)
        if old == new:
            continue
        item = {"path": name, "kind": "behavioral", "proof": "unproven change"}
        entries.append(item)
        regular = all(value is None or value[0] in {"100644", "100755"} for value in (old, new))
        executable_document = documentation(name) and any(value and value[0] == "100755" for value in (old, new))
        if not regular or executable_document or (old and new and old[0] != new[0]):
            item["proof"] = "identity or executable mode changed"
            continue
        if documentation(name):
            item.update(kind="documentation", proof="documentation path")
        elif name in TEMPLATE_TOOLS or name.startswith("templates/feature/"):
            item.update(kind="template", proof="generated-code input")
        if old and new and old[0] == new[0]:
            if simple_format(name, old[1], new[1]):
                item.update(kind="template-format" if item["kind"] == "template" else "formatting",
                            proof="structured values unchanged")
            elif Path(name.removesuffix(".tpl")).suffix == ".cs":
                pending.append(item)
                pairs.append((old[1], new[1]))
    for item, equivalent in zip(pending, proof(pairs), strict=True):
        if equivalent:
            item.update(kind="template-format" if item["kind"] == "template" else "formatting",
                        proof="C# tokens and non-whitespace trivia unchanged")
    kinds = {item["kind"] for item in entries}
    broad = "behavioral" in kinds
    template = broad or "template" in kinds
    template_format = not template and "template-format" in kinds
    return {"version": 1, "files": entries, "behavioral": broad, "template": template,
            "template_format": template_format,
            "automation": broad or any(item["path"] in TEMPLATE_TOOLS for item in entries)}


def plan(base, root=ROOT, force_full=False):
    base_sha = revision(root, base)
    diff_base = git_bytes(root, "merge-base", base_sha, "HEAD").decode().strip()
    current = working_tree(root)
    result = classify_trees(committed_tree(root, diff_base), current)
    result["fingerprint"] = fingerprint(current)
    result.update(base=base, base_sha=base_sha, diff_base_sha=diff_base, head=revision(root, "HEAD"))
    if force_full:
        result.update(behavioral=True, template=True, template_format=False, automation=True)
    result["force_full"] = force_full
    return result


def validate_ci_needs(needs):
    selection = needs["selection"]
    if selection["result"] != "success":
        raise ValueError("Scope selection/structural checks did not succeed")
    outputs = selection["outputs"]
    for name in ("behavioral", "template", "template_format", "automation"):
        if outputs.get(name) not in ("true", "false"):
            raise ValueError("Missing or invalid CI scope output: " + name)
    required = {"backend": outputs["behavioral"] == "true", "frontend": outputs["behavioral"] == "true",
                "feature-template": outputs["template"] == "true" or outputs["template_format"] == "true"}
    for job, selected in required.items():
        status = needs[job]["result"]
        if selected and status != "success":
            raise ValueError(f"Selected CI job {job} did not succeed: {status}")
        if not selected and status not in ("skipped", "success"):
            raise ValueError(f"CI job {job} failed: {status}")
    return required
