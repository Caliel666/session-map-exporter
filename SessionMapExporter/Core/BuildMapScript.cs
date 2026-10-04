namespace SessionMapExporter.Core;

/// <summary>
/// The build_map.py helper, embedded verbatim. Written once into the export
/// root; it converts every exported map into a single .glb file that Blender
/// imports natively (File -> Import -> glTF 2.0). Generated from
/// build_map.py - do not edit both copies by hand.
/// </summary>
public static class BuildMapScript
{
    public const string Content =
        """"
#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
build_map.py - Session Skate Sim map builder
============================================

Turns the raw USD (.usda) export produced by SessionMapExporter into ONE clean,
Blender-ready glTF 2.0 binary (.glb) with all geometry, hierarchy and textures
embedded in a single file.

    python build_map.py                      # build every map next to this script
    python build_map.py "Maps/LESColemen Park"
    python build_map.py --list

Then in Blender:  File -> Import -> glTF 2.0  ->  pick  Maps/<Map>/<Map>.glb

Requirements
------------
* Python 3.8+ (standard library only - no Blender, no pip packages needed).
* Optional:  pip install pillow
     - enables --max-texture-size (texture downscaling)
     - fixes the metallic/roughness channel order of packed ORM textures
       (without it those two channels are swapped on some materials)

What it does
------------
* Composes the world stage: streaming sublevels (subLayers), world references,
  mesh references, per-instance material overrides and point instancers.
* Instances every unique mesh only once in the file (shared mesh data).
* Converts Unreal space to glTF space correctly (metres, +Y up, winding, UVs).
* Skips game lights, collision/debug shapes and invisible prims.
* Writes  Maps/<Map>/<Map>.glb  plus a  build-report.json  with statistics.
"""

from __future__ import annotations

import argparse
import json
import math
import os
import re
import struct
import sys
from array import array
from pathlib import Path

# ---------------------------------------------------------------------------
# logging
# ---------------------------------------------------------------------------

_VERBOSE = True
_WARNINGS: list = []


def log(msg: str) -> None:
    if _VERBOSE:
        print(msg, flush=True)


def warn(msg: str) -> None:
    _WARNINGS.append(msg)
    if _VERBOSE:
        print("  [warn] " + msg, flush=True)


# ---------------------------------------------------------------------------
# USDA (subset) tokenizer + parser
#
# Handles exactly what CUE4Parse's UsdaWriter emits (plus generous tolerance):
#   #usda 1.0 ( (defaultPrim = "x") (metersPerUnit = 0.01) (upAxis = "Z") ... )
#   def Scope "World" ( references = [@a@] ) { attributes / rels / prims }
# ---------------------------------------------------------------------------

_TOKEN_RE = re.compile(
    r"""
    (?P<ws>[\s]+)
  | (?P<comment>\#[^\n]*)
  | (?P<string>"(?:\\.|[^"\\])*")
  | (?P<asset>@(?:\\@|[^@\\])*@)
  | (?P<path><[^<>\[\]{}()=,]*>)
  | (?P<punct>[{}\[\]()=,;])
  | (?P<ident>[A-Za-z_][A-Za-z0-9_:.]*)
  | (?P<number>[-+]?(?:\d+\.\d*(?:[eE][-+]?\d+)?|\.\d+(?:[eE][-+]?\d+)?|\d+(?:[eE][-+]?\d+)?))
    """,
    re.VERBOSE | re.DOTALL,
)

_NUM_RE = re.compile(
    r"[-+]?(?:\d+\.\d*(?:[eE][-+]?\d+)?|\.\d+(?:[eE][-+]?\d+)?|\d+(?:[eE][-+]?\d+)?)"
)
_INT_RE = re.compile(r"[-+]?\d+")


class Token:
    __slots__ = ("kind", "value", "start", "end")

    def __init__(self, kind, value, start, end):
        self.kind = kind
        self.value = value
        self.start = start
        self.end = end

    def __repr__(self):  # pragma: no cover
        return "Token(%s,%r)" % (self.kind, self.value)


_BRACKET_SCAN_RE = re.compile(r'@(?:\\@|[^@\\])*@|"(?:\\.|[^"\\])*"|[\[\]]')


def tokenize(text: str) -> list:
    tokens = []
    append = tokens.append
    pos = 0
    n = len(text)
    match = _TOKEN_RE.match
    while pos < n:
        m = match(text, pos)
        if m is None:  # unknown char - skip (robustness)
            pos += 1
            continue
        kind = m.lastgroup
        if kind == "punct" and m.group() == "[":
            # big-array fast path: capture the whole [...] as one raw token
            depth = 0
            scan = _BRACKET_SCAN_RE.finditer(text, m.start())
            end = m.end()
            for sm in scan:
                g = sm.group()
                if g == "[":
                    depth += 1
                elif g == "]":
                    depth -= 1
                    if depth == 0:
                        end = sm.end()
                        break
            append(Token("array", text[m.start():end], m.start(), end))
            pos = end
            continue
        if kind not in ("ws", "comment"):
            append(Token(kind, m.group(), m.start(), m.end()))
        pos = m.end()
    return tokens


def _unescape_string(s: str) -> str:
    return re.sub(r"\\(.)", r"\1", s[1:-1])


def _asset_path(s: str) -> str:
    return s[1:-1].replace("\\@", "@")


class RawArray:
    """Un-materialised big numeric array (fast path for huge geometry attrs)."""

    __slots__ = ("text",)

    def __init__(self, text: str):
        self.text = text

    def floats(self) -> array:
        out = array("f")
        ap = out.append
        f = float
        for m in _NUM_RE.finditer(self.text):
            ap(f(m.group()))
        return out

    def ints(self) -> array:
        out = array("i")
        ap = out.append
        i = int
        for m in _INT_RE.finditer(self.text):
            ap(i(m.group()))
        return out

    def small_values(self) -> list:
        """Nested-list view for small arrays (e.g. xformOpOrder)."""
        toks = tokenize(self.text)
        return _tokens_to_value(toks, 0)[0]


def _tokens_to_value(tokens, i):
    """Best-effort value reconstruction for small raw arrays."""
    out = []
    while i < len(tokens):
        t = tokens[i]
        if t.kind == "punct" and t.value in ")]":
            return out, i + 1
        if t.kind == "punct" and t.value in "[( [":
            pass
        if t.kind == "punct" and t.value in "[(":
            sub, i = _tokens_to_value(tokens, i + 1)
            out.append(sub)
            continue
        if t.kind == "punct" and t.value in ",;":
            i += 1
            continue
        if t.kind == "punct" and t.value == "]":
            i += 1
            continue
        if t.kind == "string":
            out.append(("__string__", _unescape_string(t.value)))
        elif t.kind == "asset":
            sub = None
            if i + 1 < len(tokens) and tokens[i + 1].kind == "path":
                i += 1
                sub = tokens[i].value[1:-1].strip()
            out.append(("__asset__", _asset_path(t.value), sub))
        elif t.kind == "path":
            out.append(("__path__", t.value[1:-1].strip()))
        elif t.kind == "number":
            out.append(int(t.value) if re.fullmatch(r"[-+]?\d+", t.value) else float(t.value))
        elif t.kind == "ident":
            v = t.value
            out.append(True if v == "true" else False if v == "false" else
                       None if v == "None" else ("__token__", v))
        i += 1
    return out, i


# ---------------------------------------------------------------------------
# parsed object model
# ---------------------------------------------------------------------------

class Attr:
    __slots__ = ("type_name", "value", "metadata")

    def __init__(self, type_name, value, metadata=None):
        self.type_name = type_name
        self.value = value
        self.metadata = metadata or {}


class Prim:
    __slots__ = ("specifier", "type_name", "name", "path", "attrs", "rels",
                 "children", "references", "parent")

    def __init__(self, specifier, type_name, name):
        self.specifier = specifier
        self.type_name = type_name
        self.name = name
        self.path = ""
        self.attrs = {}
        self.rels = {}
        self.children = []
        self.references = []      # [(assetPath, primPath-or-None), ...]
        self.parent = None

    def get(self, name, default=None):
        a = self.attrs.get(name)
        return a.value if a is not None else default

    def is_visible(self) -> bool:
        vis = self.attrs.get("visibility")
        if vis is None:
            return True
        v = vis.value
        if isinstance(v, tuple) and v and v[0] == "__string__":
            v = v[1]
        return v != "invisible"


class UsdFile:
    """A parsed .usda layer."""

    def __init__(self, path: Path):
        self.path = path
        self.default_prim = None
        self.sublayers = []
        self.roots = []
        self.by_path = {}
        self._text = ""
        self._parse(read_text_best_effort(path))

    def _parse(self, text: str):
        self._text = text
        tokens = tokenize(text)
        self._tokens = tokens
        self._i = 0

        if self._peek_is("punct", "("):
            meta = self._parse_metadata_block()
            dp = meta.get("defaultPrim")
            if isinstance(dp, tuple) and dp and dp[0] == "__string__":
                self.default_prim = dp[1]
            sl = meta.get("subLayers")
            if sl is not None:
                self.sublayers = _asset_list(sl)

        while self._peek() is not None:
            prim = self._parse_prim(None)
            if prim is not None:
                self.roots.append(prim)
                self._index(prim)
        del self._tokens

    def _index(self, prim: Prim):
        prim.path = (prim.parent.path if prim.parent else "") + "/" + prim.name
        self.by_path.setdefault(prim.path, prim)
        for c in prim.children:
            c.parent = prim
            self._index(c)

    # -- token helpers ------------------------------------------------------

    def _peek(self):
        return self._tokens[self._i] if self._i < len(self._tokens) else None

    def _advance(self):
        t = self._peek()
        self._i += 1
        return t

    def _peek_is(self, kind, value):
        t = self._peek()
        return t is not None and t.kind == kind and t.value == value

    # -- grammar ------------------------------------------------------------

    def _parse_metadata_block(self) -> dict:
        out = {}
        self._advance()  # (
        while True:
            t = self._peek()
            if t is None:
                break
            if t.kind == "punct" and t.value == ")":
                self._advance()
                break
            name = self._advance().value
            if name in ("prepend", "append", "add", "delete", "reorder") and \
                    self._peek() is not None and self._peek().kind == "ident":
                name += " " + self._advance().value
            if self._peek_is("punct", "="):
                self._advance()
            out[name] = self._parse_value_raw()
            if self._peek_is("punct", ","):
                self._advance()
        return out

    def _parse_prim(self, parent):
        spec = self._advance()
        if spec is None or spec.kind != "ident" or \
                spec.value not in ("def", "over", "class"):
            return None
        type_tok = self._advance()
        name_tok = self._advance()
        if name_tok is None or name_tok.kind != "string":
            return None
        prim = Prim(spec.value, type_tok.value if type_tok else "",
                    _unescape_string(name_tok.value))
        prim.parent = parent

        if self._peek_is("punct", "("):
            meta = self._parse_metadata_block()
            for key in ("references", "prepend references", "add references",
                        "append references"):
                if key in meta:
                    prim.references = _reference_list(meta[key])
                    break

        if not self._peek_is("punct", "{"):
            return prim
        self._advance()  # {

        while True:
            t = self._peek()
            if t is None:
                break
            if t.kind == "punct" and t.value == "}":
                self._advance()
                break

            if t.kind == "ident" and t.value in ("def", "over", "class"):
                child = self._parse_prim(prim)
                if child is not None:
                    prim.children.append(child)
                continue

            if t.kind == "ident" and t.value == "rel":
                self._advance()
                name_tok = self._advance()
                targets = []
                if name_tok is not None:
                    if self._peek_is("punct", "="):
                        self._advance()
                    targets = self._parse_path_list()
                    prim.rels[name_tok.value] = targets
                continue

            custom = False
            while self._peek() is not None and self._peek().kind == "ident" and \
                    self._peek().value in ("custom", "uniform"):
                custom = custom or self._advance().value == "custom"
            _ = custom

            type_tok = self._advance()
            if type_tok is None or type_tok.kind != "ident":
                continue
            type_name = type_tok.value
            nxt = self._peek()
            if nxt is not None and nxt.kind == "array" and nxt.value == "[]":
                self._advance()
                type_name += "[]"
            name_tok = self._advance()
            if name_tok is None or name_tok.kind != "ident":
                continue

            value = None
            if self._peek_is("punct", "="):
                self._advance()
                value = self._parse_value_raw()

            meta = {}
            if self._peek_is("punct", "("):
                meta = self._parse_metadata_block()

            prim.attrs[name_tok.value] = Attr(type_name, value, meta)
            if self._peek_is("punct", ";"):
                self._advance()
        return prim

    def _parse_path_list(self):
        t = self._peek()
        if t is None:
            return []
        if t.kind == "path":
            self._advance()
            return [t.value[1:-1].strip()]
        if t.kind == "punct" and t.value == "[":
            self._advance()
            out = []
            while True:
                t = self._peek()
                if t is None:
                    break
                if t.kind == "punct" and t.value == "]":
                    self._advance()
                    break
                if t.kind == "path":
                    self._advance()
                    out.append(t.value[1:-1].strip())
                else:
                    self._advance()
            return out
        self._advance()
        return []

    def _parse_value_raw(self):
        t = self._peek()
        if t is None:
            return None
        if t.kind == "array":
            self._advance()
            return RawArray(self._text[t.start + 1:t.end - 1])
        if t.kind == "punct" and t.value == "(":
            self._advance()
            items = []
            while True:
                t = self._peek()
                if t is None:
                    break
                if t.kind == "punct" and t.value == ")":
                    self._advance()
                    break
                items.append(self._parse_value_raw())
                if self._peek_is("punct", ","):
                    self._advance()
            return items
        self._advance()
        if t.kind == "string":
            return ("__string__", _unescape_string(t.value))
        if t.kind == "asset":
            sub = None
            nxt = self._peek()
            if nxt is not None and nxt.kind == "path":
                self._advance()
                sub = nxt.value[1:-1].strip()
            return ("__asset__", _asset_path(t.value), sub)
        if t.kind == "path":
            return ("__path__", t.value[1:-1].strip())
        if t.kind == "number":
            return int(t.value) if re.fullmatch(r"[-+]?\d+", t.value) else float(t.value)
        if t.kind == "ident":
            v = t.value
            if v == "true":
                return True
            if v == "false":
                return False
            if v == "None":
                return None
            return ("__token__", v)
        return t.value


def _asset_list(v):
    out = []
    if isinstance(v, RawArray):
        v = v.small_values()
    if isinstance(v, list):
        for item in v:
            if isinstance(item, tuple) and item and item[0] == "__asset__":
                out.append(item[1])
    elif isinstance(v, tuple) and v and v[0] == "__asset__":
        out.append(v[1])
    return out


def _reference_list(v):
    out = []
    if isinstance(v, RawArray):
        v = v.small_values()
    items = v if isinstance(v, list) else [v]
    for item in items:
        if isinstance(item, tuple) and item and item[0] == "__asset__":
            out.append((item[1], item[2]))
    return out


def read_text_best_effort(path: Path) -> str:
    for enc in ("utf-8-sig", "utf-8", "utf-16", "latin-1"):
        try:
            return path.read_text(encoding=enc)
        except (UnicodeDecodeError, UnicodeError):
            continue
        except OSError as exc:
            warn("cannot read %s: %s" % (path, exc))
            return ""
    return path.read_text(errors="replace")


# ---------------------------------------------------------------------------
# small-value helpers
# ---------------------------------------------------------------------------

def _flatten_numbers(value):
    if value is None:
        return []
    if isinstance(value, RawArray):
        return list(value.floats())
    if isinstance(value, (int, float)):
        return [float(value)]
    out = []
    for item in value:
        if isinstance(item, (int, float)):
            out.append(float(item))
        elif isinstance(item, (list, tuple)):
            out.extend(_flatten_numbers(item))
        elif isinstance(item, RawArray):
            out.extend(item.floats())
    return out


def tuple_list(value, dim):
    nums = _flatten_numbers(value)
    if not nums or dim <= 0:
        return []
    if len(nums) % dim:
        nums = nums[: len(nums) - (len(nums) % dim)]
    return [tuple(nums[i:i + dim]) for i in range(0, len(nums), dim)]


def int_list(value):
    if value is None:
        return array("i")
    if isinstance(value, RawArray):
        return value.ints()
    flat = _flatten_numbers(value)
    return array("i", (int(v) for v in flat))


def token_list(value):
    out = []
    if isinstance(value, RawArray):
        value = value.small_values()
    if isinstance(value, list):
        for item in value:
            if isinstance(item, tuple) and item and item[0] == "__token__":
                out.append(item[1])
    elif isinstance(value, tuple) and value and value[0] == "__token__":
        out.append(value[1])
    return out


# ---------------------------------------------------------------------------
# coordinate conversion (USD stage -> glTF)
#
# CUE4Parse writes the stage right-handed, Z-up, centimetres:
#     point_usd  = (x_ue, -y_ue, z_ue)        # "MIRROR_MESH"
# Its own glTF writer maps raw UE data as (x, y, z)_ue -> (x, z, -y)_ue * 0.01,
# so from stage data the equivalent map is:
#     (x, y, z)_usd -> (x, z, y)_usd * 0.01   (swap Y/Z, to metres)
#     quat (w,x,y,z)_usd -> (w, -x, -z, -y)   (conjugation through the swap)
#     translate  (x,y,z)_usd -> (x, z, y)_usd * 0.01
# Triangle winding is preserved and matches the native glTF export exactly.
# ---------------------------------------------------------------------------

METERS_PER_UNIT = 0.01


def conv_point(p):
    return (p[0] * METERS_PER_UNIT, p[2] * METERS_PER_UNIT, p[1] * METERS_PER_UNIT)


def conv_quat(q):
    w, x, y, z = q[0], q[1], q[2], q[3]
    return (w, -x, -z, -y)


def quat_to_matrix(q):
    w, x, y, z = q
    n = w * w + x * x + y * y + z * z
    if n <= 0.0:
        return (1.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0)
    s = 2.0 / n
    wx, wy, wz = s * w * x, s * w * y, s * w * z
    xx, xy, xz = s * x * x, s * x * y, s * x * z
    yy, yz, zz = s * y * y, s * y * z, s * z * z
    return (
        1.0 - (yy + zz), xy - wz, xz + wy,
        xy + wz, 1.0 - (xx + zz), yz - wx,
        xz - wy, yz + wx, 1.0 - (xx + yy),
    )


def mat3_mul(a, b):
    return (
        a[0] * b[0] + a[1] * b[3] + a[2] * b[6],
        a[0] * b[1] + a[1] * b[4] + a[2] * b[7],
        a[0] * b[2] + a[1] * b[5] + a[2] * b[8],
        a[3] * b[0] + a[4] * b[3] + a[5] * b[6],
        a[3] * b[1] + a[4] * b[4] + a[5] * b[7],
        a[3] * b[2] + a[4] * b[5] + a[5] * b[8],
        a[6] * b[0] + a[7] * b[3] + a[8] * b[6],
        a[6] * b[1] + a[7] * b[4] + a[8] * b[7],
        a[6] * b[2] + a[7] * b[5] + a[8] * b[8],
    )


def node_matrix(translation, rotation, scale):
    """T * R * S as a glTF column-major 4x4."""
    m = quat_to_matrix(rotation)
    m = (
        m[0] * scale[0], m[1] * scale[1], m[2] * scale[2],
        m[3] * scale[0], m[4] * scale[1], m[5] * scale[2],
        m[6] * scale[0], m[7] * scale[1], m[8] * scale[2],
    )
    t = translation
    return [
        m[0], m[3], m[6], 0.0,
        m[1], m[4], m[7], 0.0,
        m[2], m[5], m[8], 0.0,
        t[0], t[1], t[2], 1.0,
    ]


def read_xform(prim: Prim):
    """Reads xformOpOrder [translate, orient, scale] -> converted glTF TRS."""
    ops = token_list(prim.get("xformOpOrder"))
    if not ops:
        ops = ["xformOp:translate", "xformOp:orient", "xformOp:scale"]

    translation = (0.0, 0.0, 0.0)
    rotation = (1.0, 0.0, 0.0, 0.0)
    scale = (1.0, 1.0, 1.0)

    for op in ops:
        attr = prim.attrs.get(op)
        if attr is None or attr.value is None:
            continue
        v = attr.value
        if op.endswith(":translate"):
            t = tuple_list(v, 3)
            if t:
                translation = conv_point(t[0])
        elif op.endswith(":orient"):
            q = tuple_list(v, 4)
            if q:
                rotation = conv_quat(q[0])
        elif op.endswith(":scale"):
            s = tuple_list(v, 3)
            if s:
                scale = tuple(max(abs(c), 1e-9) for c in s[0])
        elif op.endswith(":transform"):
            m = matrix4d_rows(v)
            if m is not None:
                return matrix_to_trs(conjugate_matrix(m))
    return translation, rotation, scale


def matrix4d_rows(v):
    nums = _flatten_numbers(v)
    if len(nums) < 16:
        return None
    return [nums[i * 4:i * 4 + 4] for i in range(4)]


def conjugate_matrix(rows):
    """M' = C * M * C^-1 with C = swap(Y,Z), plus cm -> m translation scale."""
    m = [row[:] for row in rows]
    m[1], m[2] = m[2], m[1]
    for row in m:
        row[1], row[2] = row[2], row[1]
    m[0][3] *= METERS_PER_UNIT
    m[1][3] *= METERS_PER_UNIT
    m[2][3] *= METERS_PER_UNIT
    return m


def matrix_to_trs(rows):
    """Extracts (translation, rotation, scale); translation already in metres."""
    r = [rows[0][:3], rows[1][:3], rows[2][:3]]
    t = (rows[0][3], rows[1][3], rows[2][3])

    sx = math.sqrt(r[0][0] ** 2 + r[0][1] ** 2 + r[0][2] ** 2)
    sy = math.sqrt(r[1][0] ** 2 + r[1][1] ** 2 + r[1][2] ** 2)
    sz = math.sqrt(r[2][0] ** 2 + r[2][1] ** 2 + r[2][2] ** 2)
    scale = (max(sx, 1e-9), max(sy, 1e-9), max(sz, 1e-9))

    n = [[r[0][0] / scale[0], r[0][1] / scale[0], r[0][2] / scale[0]],
         [r[1][0] / scale[1], r[1][1] / scale[1], r[1][2] / scale[1]],
         [r[2][0] / scale[2], r[2][1] / scale[2], r[2][2] / scale[2]]]
    det = (n[0][0] * (n[1][1] * n[2][2] - n[1][2] * n[2][1])
           - n[0][1] * (n[1][0] * n[2][2] - n[1][2] * n[2][0])
           + n[0][2] * (n[1][0] * n[2][1] - n[1][1] * n[2][0]))
    if det < 0:
        n[0] = [-c for c in n[0]]

    x = (n[0][0], n[0][1], n[0][2])
    y = (n[1][0], n[1][1], n[1][2])
    xl = math.sqrt(x[0] ** 2 + x[1] ** 2 + x[2] ** 2) or 1.0
    x = (x[0] / xl, x[1] / xl, x[2] / xl)
    d = x[0] * y[0] + x[1] * y[1] + x[2] * y[2]
    y = (y[0] - d * x[0], y[1] - d * x[1], y[2] - d * x[2])
    yl = math.sqrt(y[0] ** 2 + y[1] ** 2 + y[2] ** 2) or 1.0
    y = (y[0] / yl, y[1] / yl, y[2] / yl)
    z = (x[1] * y[2] - x[2] * y[1],
         x[2] * y[0] - x[0] * y[2],
         x[0] * y[1] - x[1] * y[0])

    trace = x[0] + y[1] + z[2]
    if trace > 0.0:
        s = 0.5 / math.sqrt(trace + 1.0)
        w = 0.25 / s
        qx = (y[2] - z[1]) * s
        qy = (z[0] - x[2]) * s
        qz = (x[1] - y[0]) * s
    elif x[0] > y[1] and x[0] > z[2]:
        s = 2.0 * math.sqrt(1.0 + x[0] - y[1] - z[2])
        w = (y[2] - z[1]) / s
        qx = 0.25 * s
        qy = (y[0] + x[1]) / s
        qz = (z[0] + x[2]) / s
    elif y[1] > z[2]:
        s = 2.0 * math.sqrt(1.0 + y[1] - x[0] - z[2])
        w = (z[0] - x[2]) / s
        qx = (y[0] + x[1]) / s
        qy = 0.25 * s
        qz = (z[1] + y[2]) / s
    else:
        s = 2.0 * math.sqrt(1.0 + z[2] - x[0] - y[1])
        w = (x[1] - y[0]) / s
        qx = (z[0] + x[2]) / s
        qy = (z[1] + y[2]) / s
        qz = 0.25 * s
    return t, (w, qx, qy, qz), scale


# ---------------------------------------------------------------------------
# GLB writer
# ---------------------------------------------------------------------------

def _safe_name(name: str) -> str:
    cleaned = re.sub(r"[^\w \-.:+()]", "_", name or "node")
    return cleaned[:120] if cleaned else "node"


def _safe_file_name(name: str) -> str:
    return re.sub(r'[<>:"/\\|?*]', "_", name).strip() or "map"


class GLBBuilder:
    def __init__(self):
        self.buffer = bytearray()
        self.buffer_views = []
        self.accessors = []
        self.images = []
        self.textures = []
        self.materials = []
        self.meshes = []
        self.nodes = []
        self.samplers = [{}]
        self.root_nodes = []
        self._mat_cache = {}

    def add_view(self, data: bytes, target=None) -> int:
        while len(self.buffer) % 4:
            self.buffer.append(0)
        offset = len(self.buffer)
        self.buffer.extend(data)
        view = {"buffer": 0, "byteOffset": offset, "byteLength": len(data)}
        if target is not None:
            view["target"] = target
        self.buffer_views.append(view)
        return len(self.buffer_views) - 1

    def add_accessor(self, view_idx, component_type, count, type_name,
                     byte_offset=0, minmax=None) -> int:
        acc = {"bufferView": view_idx, "byteOffset": byte_offset,
               "componentType": component_type, "count": count,
               "type": type_name}
        if minmax is not None:
            acc["min"], acc["max"] = minmax
        self.accessors.append(acc)
        return len(self.accessors) - 1

    def add_texture(self, png_bytes: bytes, name: str) -> int:
        view = self.add_view(png_bytes)
        self.images.append({"bufferView": view, "mimeType": "image/png",
                            "name": _safe_name(name)})
        self.textures.append({"image": len(self.images) - 1, "sampler": 0})
        return len(self.textures) - 1

    def add_material(self, mat: dict) -> int:
        self.materials.append(mat)
        return len(self.materials) - 1

    def add_node(self, name, children=None, matrix=None, mesh=None) -> int:
        node = {"name": _safe_name(name)}
        if children:
            node["children"] = children
        if matrix is not None:
            node["matrix"] = matrix
        if mesh is not None:
            node["mesh"] = mesh
        self.nodes.append(node)
        return len(self.nodes) - 1

    def add_mesh(self, name, primitives) -> int:
        self.meshes.append({"name": _safe_name(name), "primitives": primitives})
        return len(self.meshes) - 1

    def finish(self, scene_name: str) -> bytes:
        gltf = {
            "asset": {"version": "2.0",
                      "generator": "SessionMapExporter build_map.py"},
            "scene": 0,
            "scenes": [{"name": _safe_name(scene_name), "nodes": self.root_nodes}],
            "nodes": self.nodes,
            "meshes": self.meshes,
            "accessors": self.accessors,
            "bufferViews": self.buffer_views,
            "buffers": [{"byteLength": len(self.buffer)}],
            "samplers": self.samplers,
        }
        if self.materials:
            gltf["materials"] = self.materials
        if self.textures:
            gltf["images"] = self.images
            gltf["textures"] = self.textures

        json_bytes = json.dumps(gltf, separators=(",", ":")).encode("utf-8")
        while len(json_bytes) % 4:
            json_bytes += b" "

        bin_len = len(self.buffer)
        pad = bin_len % 4
        total = 12 + 8 + len(json_bytes) + 8 + bin_len + (4 - pad if pad else 0)
        out = bytearray()
        out += struct.pack("<III", 0x46546C67, 2, total)
        out += struct.pack("<II", len(json_bytes), 0x4E4F534A)
        out += json_bytes
        out += struct.pack("<II", bin_len + (4 - pad if pad else 0), 0x004E4942)
        out += self.buffer
        if pad:
            out += b"\x00\x00\x00"
        return bytes(out)


# ---------------------------------------------------------------------------
# geometry extraction
# ---------------------------------------------------------------------------

LIGHT_TYPES = {"DistantLight", "SphereLight", "RectLight", "DiskLight",
               "DomeLight", "CylinderLight", "PortalLight", "Light"}
SHAPE_TYPES = {"Cube", "Sphere", "Capsule"}


class Geometry:
    """Static geometry of one mesh prim, already converted to glTF space."""

    __slots__ = ("key", "name", "vertex_count", "positions", "normals", "uvs",
                 "colors", "face_counts", "face_offsets", "raw_indices",
                 "subsets", "default_material", "double_sided", "triangles",
                 "source_file")

    def __init__(self, key, name):
        self.key = key
        self.name = name
        self.vertex_count = 0
        self.positions = None      # array('f') xyz
        self.normals = None        # array('f') xyz
        self.uvs = None            # array('f') uv
        self.colors = None         # array('f') rgba
        self.face_counts = array("i")
        self.face_offsets = []     # index into raw_indices per face
        self.raw_indices = array("i")
        self.subsets = []          # (name, array face ids or None, mat_ref)
        self.default_material = None
        self.double_sided = False
        self.triangles = 0


def _extract_geometry(mesh_prim: Prim, key: str) -> Geometry:
    geo = Geometry(key, mesh_prim.name)

    points = mesh_prim.get("points")
    if points is None:
        return geo
    nums = points.floats() if isinstance(points, RawArray) else array(
        "f", _flatten_numbers(points))
    n_pts = len(nums) // 3
    if n_pts == 0:
        return geo
    geo.vertex_count = n_pts

    # (x, y, z)_usd -> (x, z, y) * 0.01
    out = array("f", bytes(len(nums) * 4))
    for i in range(0, n_pts * 3, 3):
        out[i] = nums[i] * METERS_PER_UNIT
        out[i + 1] = nums[i + 2] * METERS_PER_UNIT
        out[i + 2] = nums[i + 1] * METERS_PER_UNIT
    geo.positions = out

    counts = int_list(mesh_prim.get("faceVertexCounts"))
    indices = int_list(mesh_prim.get("faceVertexIndices"))
    if len(counts) == 0:
        counts = array("i", (3,) * (len(indices) // 3))
    if len(indices) < sum(counts):
        return geo

    offset = 0
    tri = 0
    for c in counts:
        c = int(c)
        if c < 3:
            continue
        geo.face_offsets.append(offset)
        geo.face_counts.append(c)
        tri += c - 2
        offset += c
    geo.raw_indices = array("I", indices)
    geo.triangles = tri

    normals = mesh_prim.get("primvars:normals")
    if normals is not None:
        nn = normals.floats() if isinstance(normals, RawArray) else array(
            "f", _flatten_numbers(normals))
        if len(nn) // 3 == n_pts:
            nout = array("f", bytes(len(nn) * 4))
            for i in range(0, n_pts * 3, 3):
                x, z, y = nn[i], nn[i + 2], nn[i + 1]
                l = math.sqrt(x * x + y * y + z * z)
                if l > 0.0:
                    x, y, z = x / l, y / l, z / l
                else:
                    x, y, z = 0.0, 0.0, 1.0
                nout[i], nout[i + 1], nout[i + 2] = x, y, z
            geo.normals = nout

    st = mesh_prim.get("primvars:st")
    if st is not None:
        uu = st.floats() if isinstance(st, RawArray) else array(
            "f", _flatten_numbers(st))
        if len(uu) // 2 == n_pts:
            uout = array("f", bytes(len(uu) * 4))
            for i in range(0, n_pts * 2, 2):
                uout[i] = uu[i]
                uout[i + 1] = -uu[i + 1]      # USD v-up -> glTF v-down
            geo.uvs = uout

    colors = mesh_prim.get("primvars:displayColor")
    if colors is not None:
        cc = colors.floats() if isinstance(colors, RawArray) else array(
            "f", _flatten_numbers(colors))
        if len(cc) // 3 == n_pts:
            opac = mesh_prim.get("primvars:displayOpacity")
            oo = (opac.floats() if isinstance(opac, RawArray)
                  else array("f", _flatten_numbers(opac))) if opac is not None else None
            cout = array("f", bytes(n_pts * 16))
            for i in range(n_pts):
                cout[i * 4] = cc[i * 3]
                cout[i * 4 + 1] = cc[i * 3 + 1]
                cout[i * 4 + 2] = cc[i * 3 + 2]
                cout[i * 4 + 3] = oo[i] if oo is not None and i < len(oo) else 1.0
            geo.colors = cout

    ds = mesh_prim.get("doubleSided")
    geo.double_sided = bool(ds) if isinstance(ds, bool) else False

    for child in mesh_prim.children:
        if child.type_name != "GeomSubset":
            continue
        faces = int_list(child.get("indices"))
        mat_ref = None
        binding = child.rels.get("material:binding")
        if binding:
            mat_ref = ("stage", binding[0])
        geo.subsets.append((child.name, faces, mat_ref))

    own = mesh_prim.rels.get("material:binding")
    if own:
        geo.default_material = ("stage", own[0])

    if not geo.subsets:
        for child in mesh_prim.children:
            if child.type_name == "Scope" and child.name == "Materials":
                for m in child.children:
                    if m.type_name == "Material":
                        geo.subsets.append((m.name, None, ("prim", m)))
                break
    return geo


def indices_for(geo: Geometry, faces):
    """glTF index list (array('I')) for the given face ids (None = all)."""
    out = array("I")
    ext = out.extend
    if faces is None:
        for off, cnt in zip(geo.face_offsets, geo.face_counts):
            ext(geo.raw_indices[off:off + cnt])
        return out
    n_faces = len(geo.face_offsets)
    for fi in faces:
        if 0 <= fi < n_faces:
            off = geo.face_offsets[fi]
            ext(geo.raw_indices[off:off + int(geo.face_counts[fi])])
    return out

# ---------------------------------------------------------------------------
# stage composition
# ---------------------------------------------------------------------------

def _norm_path(p) -> Path:
    return Path(os.path.normpath(str(p)))


class Stage:
    def __init__(self, root_path: Path):
        self.cache = {}
        self.root_file = self._load(root_path)

    def _load(self, path: Path):
        rp = _norm_path(path)
        f = self.cache.get(rp)
        if f is None:
            if not rp.exists():
                warn("missing layer: %s" % rp)
                return None
            f = UsdFile(rp)
            self.cache[rp] = f
        return f

    def resolve_asset(self, from_file: UsdFile, asset: str):
        asset = asset.replace("\\", "/")
        if asset.startswith("./"):
            asset = asset[2:]
        p = Path(asset)
        if p.is_absolute():
            return _norm_path(p)
        return _norm_path(from_file.path.parent / asset)

    def resolve_reference(self, from_file: UsdFile, asset: str, sub_path):
        target_path = self.resolve_asset(from_file, asset)
        f = self._load(target_path)
        if f is None:
            return None, None
        prim = None
        if sub_path:
            prim = f.by_path.get(sub_path if sub_path.startswith("/") else "/" + sub_path)
        if prim is None:
            prim = f.by_path.get("/" + (f.default_prim or ""))
        if prim is None and f.roots:
            prim = f.roots[0]
        return f, prim

    def default_prim(self):
        f = self.root_file
        if f is None:
            return None
        prim = f.by_path.get("/" + (f.default_prim or ""))
        return prim if prim is not None else (f.roots[0] if f.roots else None)


# ---------------------------------------------------------------------------
# materials
# ---------------------------------------------------------------------------

class MaterialLibrary:
    def __init__(self, stage: Stage, glb: GLBBuilder):
        self.stage = stage
        self.glb = glb
        self.texture_cache = {}
        self.png_fallbacks = {}
        self.pillow = None
        try:
            import PIL.Image as _img  # noqa
            self.pillow = _img
        except Exception:
            self.pillow = None

    # -- textures -----------------------------------------------------------

    def find_png(self, from_file: UsdFile, asset: str):
        direct = self.stage.resolve_asset(from_file, asset)
        if direct.exists():
            return direct
        # ExportAllTextureMips writes T_Name_MIP{n}.png; references use T_Name
        base_name = direct.stem
        candidates = []
        parent = direct.parent
        if parent.exists():
            for cand in parent.iterdir():
                if cand.suffix.lower() != ".png":
                    continue
                m = re.fullmatch(re.escape(base_name) + r"_MIP(\d+)(?:_LAYER(\d+))?",
                                 cand.stem)
                if m:
                    candidates.append((int(m.group(1)), int(m.group(2) or 0), cand))
        if candidates:
            candidates.sort(key=lambda t: (t[0], t[1]))  # mip 0 = highest res
            return candidates[0][2]
        guess = self.png_fallbacks.get(base_name)
        return guess if (guess and guess.exists()) else None

    def texture_index(self, from_file: UsdFile, asset: str, name: str):
        png = self.find_png(from_file, asset)
        if png is None:
            warn("texture not found: %s (material %s)" % (asset, name))
            return None
        key = str(png).lower()
        if key in self.texture_cache:
            return self.texture_cache[key]
        try:
            data = png.read_bytes()
        except OSError as exc:
            warn("texture unreadable: %s (%s)" % (png, exc))
            return None
        idx = self.glb.add_texture(data, png.stem)
        self.texture_cache[key] = idx
        return idx

    def orm_texture(self, png: Path, name: str):
        """glTF wants G=roughness, B=metallic; CUE4Parse stores them the other
        way round, so swap channels when Pillow is available."""
        if self.pillow is None:
            warn("install Pillow (pip install pillow) for correct "
                 "roughness/metallic channels on %s" % png.name)
            return self.texture_index_from_bytes(png.read_bytes(), png.stem)
        try:
            import io
            from PIL import Image
            img = Image.open(png).convert("RGBA")
            r, g, b, a = img.split()
            merged = Image.merge("RGBA", (r, b, g, a))
            bio = io.BytesIO()
            merged.save(bio, "PNG")
            return self.glb.add_texture(bio.getvalue(), png.stem + "_ORM")
        except Exception as exc:
            warn("ORM swizzle failed for %s: %s" % (png, exc))
            return None

    def texture_index_from_bytes(self, data: bytes, name: str):
        if not data:
            return None
        return self.glb.add_texture(data, name)

    # -- material resolution ------------------------------------------------

    def get_material(self, mat_ref, source_file=None, double_sided=False, context=""):
        prim = self._resolve_prim(mat_ref, source_file)
        if prim is None:
            return None
        mat_file, mat_def = self._definition(prim, source_file)
        cache_key = (str(mat_file.path) if mat_file is not None else "?") + "|" + \
            (mat_def.path if mat_def is not None else prim.path)
        idx = self.glb._mat_cache.get(cache_key)
        if idx is not None:
            if double_sided and not self.glb.materials[idx].get("doubleSided"):
                self.glb.materials[idx]["doubleSided"] = True
            return idx

        mat = {"name": _safe_name(prim.name), "doubleSided": bool(double_sided)}
        pbr = {}
        alpha_mode = None
        alpha_cutoff = None

        if mat_file is not None and mat_def is not None:
            shaders = {c.name: c for c in mat_def.children if c.type_name == "Shader"}
            pbr_shader = shaders.get("PBRShader")

            def connected(slot):
                a = pbr_shader.attrs.get(slot) if pbr_shader is not None else None
                if a is None or not (isinstance(a.value, tuple) and a.value
                                     and a.value[0] == "__path__"):
                    return None
                target = a.value[1]                       # /Mat/Shader.outputs:x
                parts = target.strip("/").split("/")
                if len(parts) < 2:
                    return None
                return shaders.get(parts[1].split(".")[0])

            def texture_asset(shader_prim):
                if shader_prim is None:
                    return None
                fa = shader_prim.attrs.get("inputs:file")
                if fa is not None and isinstance(fa.value, tuple) and \
                        fa.value and fa.value[0] == "__asset__":
                    return fa.value[1]
                return None

            def tint(shader_prim):
                a = shader_prim.attrs.get("inputs:scale") if shader_prim else None
                if a is None:
                    return None
                v = tuple_list(a.value, 4)
                return v[0] if v else None

            diffuse = connected("inputs:diffuseColor.connect")
            if diffuse is not None:
                asset = texture_asset(diffuse)
                if asset:
                    ti = self.texture_index(mat_file, asset, prim.name)
                    if ti is not None:
                        pbr["baseColorTexture"] = {"index": ti}
                    tn = tint(diffuse)
                    if tn:
                        pbr["baseColorFactor"] = [tn[0], tn[1], tn[2], 1.0]
            elif pbr_shader is not None:
                a = pbr_shader.attrs.get("inputs:diffuseColor")
                if a is not None and a.value is not None:
                    c = tuple_list(a.value, 3)
                    if c:
                        pbr["baseColorFactor"] = [c[0][0], c[0][1], c[0][2], 1.0]

            threshold = pbr_shader.attrs.get("inputs:opacityThreshold") \
                if pbr_shader is not None else None
            if threshold is not None and threshold.value is not None:
                alpha_mode = "MASK"
                try:
                    alpha_cutoff = float(threshold.value)
                except (TypeError, ValueError):
                    alpha_cutoff = 0.333
            elif connected("inputs:opacity.connect") is not None:
                alpha_mode = "BLEND"

            normal = connected("inputs:normal.connect")
            if normal is not None:
                asset = texture_asset(normal)
                if asset:
                    ti = self.texture_index(mat_file, asset, prim.name)
                    if ti is not None:
                        mat["normalTexture"] = {"index": ti}

            rough = connected("inputs:roughness.connect")
            metal = connected("inputs:metallic.connect")
            if rough is not None and metal is not None:
                asset = texture_asset(rough)
                if asset:
                    png = self.find_png(mat_file, asset)
                    if png is not None:
                        ti = self.orm_texture(png, prim.name)
                        if ti is not None:
                            pbr["metallicRoughnessTexture"] = {"index": ti}
                pbr.setdefault("roughnessFactor", 1.0)
                pbr.setdefault("metallicFactor", 1.0)
            elif pbr_shader is not None:
                a = pbr_shader.attrs.get("inputs:roughness")
                if a is not None and isinstance(a.value, (int, float)):
                    pbr["roughnessFactor"] = float(a.value)
                a = pbr_shader.attrs.get("inputs:metallic")
                if a is not None and isinstance(a.value, (int, float)):
                    pbr["metallicFactor"] = float(a.value)

            emissive = connected("inputs:emissiveColor.connect")
            if emissive is not None:
                asset = texture_asset(emissive)
                if asset:
                    ti = self.texture_index(mat_file, asset, prim.name)
                    if ti is not None:
                        mat["emissiveTexture"] = {"index": ti}
                        tn = tint(emissive)
                        mat["emissiveFactor"] = [tn[0], tn[1], tn[2]] if tn \
                            else [1.0, 1.0, 1.0]

        if "baseColorFactor" not in pbr and "baseColorTexture" not in pbr:
            pbr["baseColorFactor"] = [0.72, 0.72, 0.72, 1.0]
        if "roughnessFactor" not in pbr and "metallicRoughnessTexture" not in pbr:
            pbr["roughnessFactor"] = 0.9
        if "metallicFactor" not in pbr and "metallicRoughnessTexture" not in pbr:
            pbr["metallicFactor"] = 0.0
        mat["pbrMetallicRoughness"] = pbr
        if alpha_mode:
            mat["alphaMode"] = alpha_mode
            if alpha_mode == "MASK" and alpha_cutoff is not None:
                mat["alphaCutoff"] = alpha_cutoff

        idx = self.glb.add_material(mat)
        self.glb._mat_cache[cache_key] = idx
        return idx

    def _resolve_prim(self, mat_ref, source_file=None):
        if mat_ref is None:
            return None
        kind = mat_ref[0]
        if kind == "prim":
            return mat_ref[1]
        if kind == "stage":
            f = source_file if source_file is not None else self.stage.root_file
            return f.by_path.get(mat_ref[1]) if f is not None else None
        if kind == "file":
            f, sub = mat_ref[1], mat_ref[2]
            return f.by_path.get(sub) if sub else (f.roots[0] if f.roots else None)
        return None

    def _definition(self, prim: Prim, start_file=None):
        """Follows a Material prim's references to its definition file."""
        current_file = start_file if start_file is not None else self.stage.root_file
        current_prim = prim
        hops = 0
        while current_prim is not None and current_prim.references and hops < 8:
            asset, sub = current_prim.references[0]
            if current_file is None:
                return None, None
            f, p = self.stage.resolve_reference(current_file, asset, sub)
            if p is None:
                break
            current_file, current_prim = f, p
            hops += 1
        return current_file, current_prim


# ---------------------------------------------------------------------------
# scene walker
# ---------------------------------------------------------------------------

class SceneBuilder:
    def __init__(self, stage: Stage, glb: GLBBuilder, materials: MaterialLibrary):
        self.stage = stage
        self.glb = glb
        self.materials = materials
        self.geo_cache = {}      # stage file path + prim path -> Geometry
        self.mesh_cache = {}     # geo key + instance key -> glTF mesh index
        self.stats = {
            "nodes": 0, "mesh_instances": 0, "instanced_points": 0,
            "unique_meshes": 0, "empty_meshes": 0, "triangles": 0,
            "skipped_lights": 0, "skipped_shapes": 0, "skipped_invisible": 0,
            "unresolved": 0,
        }

    # -- reference resolution ------------------------------------------------

    def _effective(self, prim: Prim, from_file: UsdFile):
        f, p = from_file, prim
        for _ in range(6):
            if not p.references:
                return f, p
            asset, sub = p.references[0]
            nf, np_ = self.stage.resolve_reference(f, asset, sub)
            if np_ is None:
                return None, None
            f, p = nf, np_
        return f, p

    # -- geometry / mesh handling --------------------------------------------

    def _geometry_of(self, file: UsdFile, mesh_prim: Prim):
        key = "%s|%s" % (file.path, mesh_prim.path)
        geo = self.geo_cache.get(key)
        if geo is None:
            geo = _extract_geometry(mesh_prim, key)
            geo.source_file = file
            self.geo_cache[key] = geo
        return geo

    def _gltf_mesh(self, geo: Geometry, instance_prim, instance_file, name):
        if geo.positions is None or not geo.face_offsets:
            self.stats["empty_meshes"] += 1
            return None
        cache_key = "%s|%s" % (
            geo.key,
            (str(instance_file.path) + instance_prim.path) if instance_prim is not None else "-")
        mesh_idx = self.mesh_cache.get(cache_key)
        if mesh_idx is not None:
            return mesh_idx

        n_pts = geo.vertex_count
        mins = [geo.positions[i] for i in (0, 1, 2)]
        maxs = list(mins)
        p = geo.positions
        for i in range(3, n_pts * 3, 3):
            for k in range(3):
                v = p[i + k]
                if v < mins[k]:
                    mins[k] = v
                elif v > maxs[k]:
                    maxs[k] = v

        pos_view = self.glb.add_view(p.tobytes(), 34962)
        pos_acc = self.glb.add_accessor(pos_view, 5126, n_pts, "VEC3", 0,
                                        ([mins[0], mins[1], mins[2]],
                                         [maxs[0], maxs[1], maxs[2]]))
        attrs = {"POSITION": pos_acc}
        if geo.normals is not None:
            attrs["NORMAL"] = self.glb.add_accessor(
                self.glb.add_view(geo.normals.tobytes(), 34962), 5126, n_pts, "VEC3")
        if geo.uvs is not None:
            attrs["TEXCOORD_0"] = self.glb.add_accessor(
                self.glb.add_view(geo.uvs.tobytes(), 34962), 5126, n_pts, "VEC2")
        if geo.colors is not None:
            attrs["COLOR_0"] = self.glb.add_accessor(
                self.glb.add_view(geo.colors.tobytes(), 34962), 5126, n_pts, "VEC4")

        primitives = []
        subsets = geo.subsets if geo.subsets else [(None, None, None)]
        covered = set()
        for _n, faces, _m in geo.subsets:
            if faces:
                covered.update(faces)

        for s_name, faces, _mat in subsets:
            idx_list = indices_for(geo, faces)
            if not len(idx_list):
                continue
            mat_idx = self._material_for(geo, s_name, instance_prim, instance_file)
            idx_acc = self.glb.add_accessor(
                self.glb.add_view(idx_list.tobytes(), 34963), 5125,
                len(idx_list), "SCALAR")
            prim = {"mode": 4, "indices": idx_acc, "attributes": dict(attrs)}
            if mat_idx is not None:
                prim["material"] = mat_idx
            primitives.append(prim)

        if geo.subsets and covered:
            missing = array("i", (fi for fi in range(len(geo.face_offsets))
                                  if fi not in covered))
            idx_list = indices_for(geo, missing)
            if len(idx_list):
                idx_acc = self.glb.add_accessor(
                    self.glb.add_view(idx_list.tobytes(), 34963), 5125,
                    len(idx_list), "SCALAR")
                primitives.append({"mode": 4, "indices": idx_acc,
                                   "attributes": dict(attrs)})

        if not primitives:
            self.stats["empty_meshes"] += 1
            return None

        self.stats["unique_meshes"] += 1
        self.stats["triangles"] += geo.triangles
        mesh_idx = self.glb.add_mesh(name, primitives)
        self.mesh_cache[cache_key] = mesh_idx
        return mesh_idx

    def _material_for(self, geo: Geometry, subset_name, instance_prim, instance_file):
        if instance_prim is not None:
            for c in instance_prim.children:
                if c.type_name == "GeomSubset" and c.name == subset_name:
                    binding = c.rels.get("material:binding")
                    if binding:
                        target = instance_file.by_path.get(binding[0]) \
                            if instance_file is not None else None
                        if target is not None:
                            idx = self.materials.get_material(
                                ("prim", target), instance_file,
                                geo.double_sided, instance_prim.name)
                            if idx is not None:
                                return idx
        for s_name, _faces, mat_ref in geo.subsets:
            if s_name == subset_name:
                return self.materials.get_material(mat_ref, geo.source_file,
                                                   geo.double_sided, geo.name)
        return self.materials.get_material(geo.default_material, geo.source_file,
                                           geo.double_sided, geo.name)

    # -- traversal -------------------------------------------------------------

    def walk(self, prim: Prim, file: UsdFile, parent_children: list, depth=0):
        if depth > 24 or prim is None:
            return
        if not prim.is_visible():
            self.stats["skipped_invisible"] += 1
            return

        t = prim.type_name
        if t in LIGHT_TYPES:
            self.stats["skipped_lights"] += 1
            return
        if t in SHAPE_TYPES:
            self.stats["skipped_shapes"] += 1
            return

        if t == "PointInstancer":
            self._walk_instancer(prim, file, parent_children, depth)
            return

        if t in ("Mesh", "SkelRoot") or prim.get("points") is not None \
                or prim.references:
            self._walk_mesh(prim, file, parent_children, depth)
            return

        children = []
        for child in prim.children:
            self.walk(child, file, children, depth + 1)
        node_idx = self.glb.add_node(prim.name, children=children or None)
        parent_children.append(node_idx)
        self.stats["nodes"] += 1

    def _walk_mesh(self, prim: Prim, file: UsdFile, parent_children: list, depth):
        f, target = self._effective(prim, file)
        if target is None:
            self.stats["unresolved"] += 1
            warn("unresolved mesh reference on '%s'" % prim.name)
            return

        if target.type_name == "SkelRoot":
            target = next((c for c in target.children if c.type_name == "Mesh"), None)
            if target is None:
                self.stats["unresolved"] += 1
                return

        if target.get("points") is None:
            # world/level reference or empty scope: splice its children
            children = []
            if target is not prim:
                for child in target.children:
                    self.walk(child, f, children, depth + 1)
            for child in prim.children:
                self.walk(child, file, children, depth + 1)
            if children:
                # apply the level-reference transform (usually identity)
                t_, r_, s_ = read_xform(prim)
                node_idx = self.glb.add_node(
                    prim.name, children=children,
                    matrix=node_matrix(t_, r_, s_))
                parent_children.append(node_idx)
                self.stats["nodes"] += 1
            return

        geo = self._geometry_of(f, target)
        mesh_idx = self._gltf_mesh(geo, prim, file, target.name)
        if mesh_idx is None:
            return
        t, r, s = read_xform(prim)
        node_idx = self.glb.add_node(prim.name, matrix=node_matrix(t, r, s),
                                     mesh=mesh_idx)
        parent_children.append(node_idx)
        self.stats["mesh_instances"] += 1
        self.stats["nodes"] += 1

        kids = []
        for child in prim.children:
            if child.type_name == "GeomSubset" or \
                    (child.type_name == "Scope" and child.name == "OverrideMaterials"):
                continue
            self.walk(child, file, kids, depth + 1)
        if kids:
            self.glb.nodes[node_idx]["children"] = kids

    def _walk_instancer(self, prim: Prim, file: UsdFile, parent_children: list, depth):
        protos = []
        for path in prim.rels.get("prototypes") or []:
            p = file.by_path.get(path)
            if p is not None:
                protos.append(p)
        if not protos:
            self.stats["unresolved"] += 1
            warn("point instancer '%s' has no resolvable prototypes" % prim.name)
            return

        proto_meshes = []
        for proto in protos:
            f, target = self._effective(proto, file)
            if target is not None and target.type_name == "SkelRoot":
                target = next((c for c in target.children
                               if c.type_name == "Mesh"), None)
            if target is None or target.get("points") is None:
                proto_meshes.append(None)
                continue
            geo = self._geometry_of(f, target)
            proto_meshes.append(self._gltf_mesh(geo, proto, file, target.name))

        proto_indices = int_list(prim.get("protoIndices"))
        positions = prim.get("positions")
        orientations = prim.get("orientations")
        scales = prim.get("scales")
        positions = positions.floats() if isinstance(positions, RawArray) else \
            array("f", _flatten_numbers(positions))
        orientations = orientations.floats() if isinstance(orientations, RawArray) \
            else array("f", _flatten_numbers(orientations))
        scales = scales.floats() if isinstance(scales, RawArray) else \
            array("f", _flatten_numbers(scales))

        count = len(positions) // 3
        inst_children = []
        for i in range(count):
            mesh_idx = proto_meshes[proto_indices[i]] \
                if i < len(proto_indices) and proto_indices[i] < len(proto_meshes) else None
            if mesh_idx is None:
                continue
            px = positions[i * 3] * METERS_PER_UNIT
            py = positions[i * 3 + 2] * METERS_PER_UNIT
            pz = positions[i * 3 + 1] * METERS_PER_UNIT
            if i * 4 + 3 < len(orientations):
                q = conv_quat(orientations[i * 4:i * 4 + 4])
            else:
                q = (1.0, 0.0, 0.0, 0.0)
            if i * 3 + 2 < len(scales):
                s = (max(abs(scales[i * 3]), 1e-9),
                     max(abs(scales[i * 3 + 1]), 1e-9),
                     max(abs(scales[i * 3 + 2]), 1e-9))
            else:
                s = (1.0, 1.0, 1.0)
            node = {"name": _safe_name("%s_%d" % (prim.name, i)),
                    "matrix": node_matrix((px, py, pz), q, s), "mesh": mesh_idx}
            self.glb.nodes.append(node)
            inst_children.append(len(self.glb.nodes) - 1)
            self.stats["instanced_points"] += 1

        node_idx = self.glb.add_node(prim.name, children=inst_children or None)
        parent_children.append(node_idx)
        self.stats["nodes"] += 1


# ---------------------------------------------------------------------------
# map discovery
# ---------------------------------------------------------------------------

def find_map_dirs(base: Path):
    maps = base / "Maps"
    if maps.is_dir():
        return sorted(p for p in maps.iterdir() if p.is_dir())
    if (base / "source").is_dir() or (base / "export-manifest.json").exists():
        return [base]
    return []


def discover_world_file(map_dir: Path):
    """The persistent world = largest Scope-rooted .usda (or manifest-listed)."""
    manifest = map_dir / "export-manifest.json"
    listed = []
    if manifest.exists():
        try:
            data = json.loads(read_text_best_effort(manifest))
            for rel in data.get("sourceWorlds") or []:
                p = map_dir / rel
                if p.exists():
                    listed.append(p)
        except Exception as exc:
            warn("manifest unreadable: %s" % exc)
    if listed:
        listed.sort(key=lambda p: p.stat().st_size, reverse=True)
        return listed[0]

    src = map_dir / "source"
    search_root = src if src.is_dir() else map_dir
    best, best_size = None, -1
    for p in search_root.rglob("*.usda"):
        try:
            size = p.stat().st_size
        except OSError:
            continue
        if size <= best_size:
            continue
        head = read_text_best_effort(p)[:4000]
        if "def Scope" in head:
            best, best_size = p, size
    if best is None:
        for p in search_root.rglob("*.usda"):
            try:
                size = p.stat().st_size
            except OSError:
                continue
            if size > best_size:
                best, best_size = p, size
    return best


# ---------------------------------------------------------------------------
# build
# ---------------------------------------------------------------------------

def build_map(map_dir: Path, out_path: Path = None) -> dict:
    global _WARNINGS
    _WARNINGS = []

    map_name = map_dir.name
    log("")
    log("=== %s ===" % map_name)

    world_file = discover_world_file(map_dir)
    if world_file is None:
        warn("no .usda world file found - skipping")
        return {"map": map_name, "ok": False, "error": "no world usda"}

    try:
        rel = world_file.relative_to(map_dir)
    except ValueError:
        rel = world_file
    log("world stage: %s" % rel)

    stage = Stage(world_file)
    glb = GLBBuilder()
    materials = MaterialLibrary(stage, glb)
    scene = SceneBuilder(stage, glb, materials)

    children = []
    root_prim = stage.default_prim()
    if root_prim is not None:
        for child in root_prim.children:
            scene.walk(child, stage.root_file, children)
    elif stage.root_file is not None:
        for rp in stage.root_file.roots:
            scene.walk(rp, stage.root_file, children)

    if stage.root_file is not None:
        for asset in stage.root_file.sublayers:
            sub_file = stage._load(stage.resolve_asset(stage.root_file, asset))
            if sub_file is not None:
                for rp in sub_file.roots:
                    scene.walk(rp, sub_file, children)

    root_idx = glb.add_node(map_name, children=children or None)
    glb.root_nodes.append(root_idx)

    if out_path is None:
        out_path = map_dir / (_safe_file_name(map_name) + ".glb")
    data = glb.finish(map_name)
    out_path.write_bytes(data)

    report = {
        "map": map_name,
        "ok": True,
        "output": str(out_path),
        "size_mb": round(len(data) / (1024 * 1024), 2),
        "world_stage": str(world_file),
        "stats": scene.stats,
        "materials": len(glb.materials),
        "textures": len(glb.images),
        "warnings": list(_WARNINGS),
    }
    (map_dir / "build-report.json").write_text(
        json.dumps(report, indent=2), encoding="utf-8")

    st = scene.stats
    log("nodes=%d unique_meshes=%d instances=%d instanced_points=%d "
        "triangles=%d materials=%d textures=%d"
        % (st["nodes"], st["unique_meshes"], st["mesh_instances"],
           st["instanced_points"], st["triangles"], len(glb.materials),
           len(glb.images)))
    log("output: %s  (%.2f MB)" % (out_path, report["size_mb"]))
    if _WARNINGS:
        log("%d warning(s) - see build-report.json" % len(_WARNINGS))
    return report


def main(argv=None) -> int:
    global _VERBOSE
    ap = argparse.ArgumentParser(
        description="Build Session map exports into single .glb files")
    ap.add_argument("map", nargs="?",
                    help='map folder, e.g. "Maps/LESColemen Park"')
    ap.add_argument("--list", action="store_true", help="list maps and exit")
    ap.add_argument("-o", "--out", help="explicit .glb output path (single map)")
    ap.add_argument("--max-texture-size", type=int, default=0, metavar="N",
                    help="downscale textures to N px (requires Pillow)")
    ap.add_argument("--quiet", action="store_true")
    args = ap.parse_args(argv)
    _VERBOSE = not args.quiet

    script_dir = Path(__file__).resolve().parent

    if args.map:
        target = Path(args.map)
        if not target.is_absolute():
            target = script_dir / target
        map_dirs = [target]
    else:
        map_dirs = find_map_dirs(script_dir)

    if args.list:
        for m in map_dirs:
            print(m.name)
        return 0

    if not map_dirs:
        print("No maps found.\n"
              "Run this script from your export folder (next to Maps/) or pass "
              "a map folder:\n"
              '  python build_map.py "Maps/LESColemen Park"')
        return 1

    if args.max_texture_size:
        try:
            import PIL  # noqa
        except ImportError:
            print("--max-texture-size requires Pillow: pip install pillow")
            return 2
        _patch_texture_scaling(args.max_texture_size)

    built = failed = 0
    for m in map_dirs:
        if not m.is_dir():
            print("not a folder: %s" % m)
            failed += 1
            continue
        try:
            out = Path(args.out) if (args.out and len(map_dirs) == 1) else None
            if build_map(m, out).get("ok"):
                built += 1
            else:
                failed += 1
        except Exception:
            import traceback
            traceback.print_exc()
            print("FAILED %s" % m.name)
            failed += 1

    print("")
    print("Done. %d built, %d failed. Import the .glb via "
          "Blender: File -> Import -> glTF 2.0" % (built, failed))
    return 0 if failed == 0 else 1


def _patch_texture_scaling(n: int):
    import io
    from PIL import Image

    def scaled(self, from_file, asset, name):
        png = self.find_png(from_file, asset)
        if png is None:
            warn("texture not found: %s" % asset)
            return None
        key = ("%s|%d" % (png, n)).lower()
        if key in self.texture_cache:
            return self.texture_cache[key]
        try:
            img = Image.open(png).convert("RGBA")
            if max(img.size) > n:
                img.thumbnail((n, n), Image.LANCZOS)
            bio = io.BytesIO()
            img.save(bio, "PNG")
            data = bio.getvalue()
        except Exception as exc:
            warn("texture scale failed for %s: %s" % (png, exc))
            data = png.read_bytes()
        idx = self.glb.add_texture(data, png.stem)
        self.texture_cache[key] = idx
        return idx

    MaterialLibrary.texture_index = scaled


if __name__ == "__main__":
    sys.exit(main())
"""" + "\n";
}
