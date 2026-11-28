#!/usr/bin/env python3
# Copyright (C) 2026 Victor Shnaider and vPole contributors
# SPDX-License-Identifier: AGPL-3.0-only

"""Materialize the Pole metadata-feature starter without wiring or overwriting user code."""

import argparse
from datetime import datetime, timezone
import hashlib
from pathlib import Path
import re
import shutil

ROOT = Path(__file__).resolve().parents[1]
TEMPLATE = ROOT / "templates/feature"
# This initial schema release precedes later migrations; the suffix identifies the feature.
INITIAL_SCHEMA_RELEASE = datetime(2026, 11, 7, tzinfo=timezone.utc)


def initial_migration_id(feature, route):
    timestamp = f"{int(INITIAL_SCHEMA_RELEASE.timestamp()):08x}"
    identity = hashlib.sha256(f"{feature}/{route}/initial-schema-v1".encode()).hexdigest()[:16]
    return timestamp + identity


def create_feature(feature, resource, route, root=ROOT, template=TEMPLATE):
    for name in (feature, resource):
        if not re.fullmatch(r"[A-Z][A-Za-z0-9]{1,63}", name) or name in {"FeatureName", "ResourceName"}:
            raise ValueError("Feature and resource must be PascalCase identifiers of 2–64 characters.")
    if not re.fullmatch(r"[a-z][a-z0-9]*(?:-[a-z0-9]+)*", route) or len(route) > 63:
        raise ValueError("Route must be a lowercase kebab-case segment of at most 63 characters.")
    root = root.resolve()
    parent = root / "src/features"
    if not parent.resolve().is_relative_to(root):
        raise ValueError("Feature output must remain inside the selected repository root.")
    destination = parent / feature
    replacements = {
        "FeatureName": feature,
        "ResourceName": resource,
        "feature-route": route,
        "__MIGRATION_ID__": initial_migration_id(feature, route),
    }
    tokens = re.compile("|".join(re.escape(key) for key in replacements))

    def render(value):
        return tokens.sub(lambda match: replacements[match.group()], value)

    files = {}
    for source in sorted(template.rglob("*.tpl")):
        if source.is_symlink():
            raise ValueError("Template inputs must be regular files.")
        relative = Path(render(source.relative_to(template).as_posix()[:-4]))
        if relative in files:
            raise ValueError(f"Template paths collide after renaming: {relative}")
        files[relative] = render(source.read_text())
    if not files:
        raise ValueError("Feature template contains no source files.")
    parent.mkdir(parents=True, exist_ok=True)
    destination.mkdir()  # Reserve atomically; a concurrent creator must never be overwritten.
    try:
        for relative, content in files.items():
            target = destination / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            with target.open("x") as output:
                output.write(content)
    except BaseException:
        shutil.rmtree(destination)
        raise
    return destination


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--feature", required=True)
    parser.add_argument("--resource", required=True)
    parser.add_argument("--route", required=True)
    args = parser.parse_args()
    try:
        result = create_feature(args.feature, args.resource, args.route)
    except (ValueError, OSError) as error:
        parser.exit(1, f"{error}\n")
    print(f"Created {result.relative_to(ROOT)}. Follow its README for explicit composition and validation.")


if __name__ == "__main__":
    main()
