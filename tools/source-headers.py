#!/usr/bin/env python3
# ============================================================================
# OPENFACTOR TECHNOLOGY GROUP
# CONFIDENTIAL AND PROPRIETARY SOFTWARE
# ============================================================================
# Copyright (c) 2026 OpenFactor Technology Group.
# All rights reserved.
# PRODUCT       : INSYS
# COMPONENT     : Tools
# CONFIDENTIALITY AND INTELLECTUAL PROPERTY NOTICE
# This source code and its associated documentation contain confidential and
# proprietary information belonging to OpenFactor Technology Group or its
# applicable intellectual property rights holder.
# This material is provided solely for authorised development, maintenance,
# operation or evaluation purposes, as applicable. No person may reproduce,
# modify, distribute, disclose, publish or otherwise use this material except
# as expressly permitted by an applicable written agreement or licence.
# No ownership rights or licence to any intellectual property are granted
# by the inclusion of this notice, except as expressly provided in an
# applicable written agreement or licence.
# Third-party software, open-source components and customer-owned materials
# remain subject to their respective ownership rights and applicable licences
# or agreements.
# UNAUTHORISED ACCESS, USE, COPYING OR DISCLOSURE IS PROHIBITED.
# ============================================================================
"""Adds or checks the OpenFactor source file header (Phase 8, Workstream C).

Usage: python3 tools/source-headers.py --check   (exit 1 if any in-scope file lacks the header or names the wrong component)
       python3 tools/source-headers.py --apply   (adds missing headers and corrects components)

The header text is tools/source-header.txt; {COMPONENT} is the module or component the file belongs to. In scope: C#,
SQL, project and build files (.csproj, .props, .targets, .slnx), YAML, shell, PowerShell, Python and Dockerfiles that
git tracks, outside bin/ and obj/. Out of scope: JSON, Markdown and other documentation, OpenAPI snapshots, test data,
lock files and third-party files. tests/Insys.ArchitectureTests/SourceHeaderTests.cs applies the same rules.
"""
import pathlib
import re
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
TEMPLATE = (ROOT / "tools" / "source-header.txt").read_text(encoding="utf-8").rstrip("\n").split("\n")
C_STYLE = {".cs", ".sql"}
HASH_STYLE = {".yml", ".yaml", ".sh", ".ps1", ".py"}
XML_STYLE = {".csproj", ".props", ".targets", ".slnx"}
MARKER = "OPENFACTOR TECHNOLOGY GROUP"


def style(path):
    if path.name.startswith("Dockerfile"):
        return "hash"
    suffix = path.suffix.lower()
    return "c" if suffix in C_STYLE else "hash" if suffix in HASH_STYLE else "xml" if suffix in XML_STYLE else None


def component(rel):
    parts = rel.split("/")
    if parts[0] == "src" and parts[1] == "Modules":
        return parts[2]
    if parts[0] == "src" and parts[1] == "BuildingBlocks":
        return parts[2].removeprefix("Insys.")
    if parts[0] == "src" and parts[1] == "Host":
        return "Api.Host"
    if parts[0] == "tools":
        return parts[1].removeprefix("Insys.") if len(parts) > 2 else "Tools"
    if parts[0] == "tests":
        return parts[1].removeprefix("Insys.") if len(parts) > 2 else "Tests.Build"
    if parts[0] == "db":
        return {"migrations": "Migrations", "roles": "Database.Roles", "bootstrap": "Database.Bootstrap"}.get(parts[1], "Database")
    if parts[0] == ".github":
        return "CI"
    if rel.startswith("docker-compose"):
        return "Local.Environment"
    return "Build"


def render(kind, name):
    lines = [line.replace("{COMPONENT}", name) for line in TEMPLATE]
    if kind == "c":
        return "/*\n" + "".join(f" * {line}\n" for line in lines) + " */\n"
    if kind == "hash":
        return "".join(f"# {line}\n" for line in lines)
    return "<!--\n" + "".join(f"  {line}\n" for line in lines) + "-->\n"


def split_preamble(text):
    """Returns (preamble, rest): a byte-order mark, a shebang line and an XML declaration stay before the header."""
    preamble = ""
    if text.startswith("﻿"):
        preamble, text = "﻿", text[1:]
    for pattern in (r"#![^\n]*\n", r"<\?xml[^>]*\?>\n"):
        match = re.match(pattern, text)
        if match:
            preamble += match.group(0)
            text = text[match.end():]
    return preamble, text


def existing_header(kind, rest):
    """The header block at the start of rest (its fixed number of lines), if there is one; None otherwise."""
    if MARKER not in rest[:400]:
        return None
    count = len(TEMPLATE) + (0 if kind == "hash" else 2)
    lines = rest.split("\n")
    return "\n".join(lines[:count]) + "\n" if len(lines) > count else None


def in_scope():
    output = subprocess.run(["git", "ls-files", "--cached", "--others", "--exclude-standard"], cwd=ROOT, check=True,
                            capture_output=True, text=True).stdout
    for rel in sorted(set(output.splitlines())):
        path = ROOT / rel
        if "/bin/" in f"/{rel}" or "/obj/" in f"/{rel}" or not path.is_file():
            continue
        kind = style(path)
        if kind:
            yield rel, path, kind


def main():
    apply = "--apply" in sys.argv
    problems = []
    changed = 0
    for rel, path, kind in in_scope():
        text = path.read_text(encoding="utf-8")
        preamble, rest = split_preamble(text)
        expected = render(kind, component(rel))
        if rest.startswith(expected):
            continue
        current = existing_header(kind, rest)
        problems.append(f"{rel}: {'wrong header or component' if current else 'no header'} (expected COMPONENT {component(rel)})")
        if apply:
            body = rest[len(current):] if current else rest
            path.write_text(preamble + expected + body, encoding="utf-8", newline="")
            changed += 1
    if apply:
        print(f"{changed} file(s) updated")
        return 0
    for problem in problems:
        print(problem)
    print(f"{len(problems)} file(s) without the correct header")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
