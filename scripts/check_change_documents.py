#!/usr/bin/env python3
# Copyright (C) 2026 Victor Shnaider and vPole contributors
# SPDX-License-Identifier: AGPL-3.0-only

"""Validate changed documentation links, anchors and skill metadata without application tests."""

import argparse
from pathlib import Path
import re
from urllib.parse import unquote, urlsplit

from verification_scope import ROOT, committed_tree, documentation, git_bytes, working_tree


def body(text):
    return re.sub(r"(?ms)^(`{3,}|~{3,}).*?^\1[^\n]*$", "", text)


def anchors(text):
    counts, result = {}, set()
    for heading in re.findall(r"(?m)^#{1,6}\s+(.+?)\s*#*\s*$", body(text)):
        slug = re.sub(r"[^\w\- ]", "", heading.replace("`", "").lower()).replace(" ", "-")
        index = counts.get(slug, 0)
        counts[slug] = index + 1
        result.add(slug + (f"-{index}" if index else ""))
    result.update(re.findall(r'<a\s+(?:id|name)=["\']([^"\']+)', text))
    return result


def check(base, root=ROOT):
    before, after = committed_tree(root, base), working_tree(root)
    changed = {name for name in before.keys() | after.keys() if before.get(name) != after.get(name)}
    sources = {name for name in changed if documentation(name) and name in after}
    # A deleted/renamed target can break an unchanged README too.
    incoming = {name for name in after if documentation(name)}
    count = 0
    for name in sorted(sources | incoming):
        text = after[name][1].decode("utf-8-sig")
        if name.endswith("/SKILL.md") and name in sources:
            metadata = re.match(r"\A---\s*\n(.*?)\n---(?:\n|$)", text, re.S)
            if not metadata or any(not re.search(rf"(?m)^{key}:\s*\S.+$", metadata[1])
                                   for key in ("name", "description")):
                raise ValueError(f"Invalid skill metadata: {name}")
        logical = root / ("src/features/FeatureTemplateProbe/README.md"
                          if name == "templates/feature/README.md.tpl" else name)
        for target in re.findall(r"\[[^\]\n]*\]\(<?([^\s)>]+)>?(?:\s+[^)]*)?\)", body(text)):
            url = urlsplit(target)
            if url.scheme or url.netloc:
                continue
            destination = (logical.parent / unquote(url.path)).resolve() if url.path else logical
            if not destination.is_relative_to(root.resolve()):
                raise ValueError(f"Documentation link escapes repository: {name}: {target}")
            relative = destination.relative_to(root.resolve()).as_posix()
            if name not in sources and relative not in changed:
                continue
            count += 1
            if relative not in after and not destination.is_dir():
                raise ValueError(f"Missing local documentation target: {name}: {target}")
            if url.fragment and relative in after and Path(relative).suffix == ".md":
                if unquote(url.fragment) not in anchors(after[relative][1].decode("utf-8-sig")):
                    raise ValueError(f"Missing documentation anchor: {name}: {target}")
    print(f"Validated {count} local documentation links and changed skill metadata.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base", required=True)
    args = parser.parse_args()
    check(args.base)


if __name__ == "__main__":
    main()
