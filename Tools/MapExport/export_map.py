"""
Exports a Valorant map .blend (FModel / UEFormat import, Blender 4.2) for ValoReplay's Unity map builder.

    blender -b "Ascent (blender 4.2).blend" --python export_map.py -- --out MapExports/Ascent

Writes to --out:
    meshes.bin   every unique mesh once (instances share it), in Blender local coordinates
    map.json     materials (textures + BaseEnv parameters), meshes, and every placed object (world matrix,
                 collection, material per slot)
    textures/    the packed images the materials use, written byte-for-byte

Coordinates stay in Blender space (metres, Z-up, Y = -Unreal Y). The Unity builder converts them
(Unity = (-y, z, x)); ValoReplay's replays line up with that exactly.

meshes.bin layout (little endian):
    char[8] "VRMESH01", int32 meshCount, then per mesh:
    int32 nameLen, utf8 name, int32 vertexCount, int32 flags (1 = uv1, 2 = colors),
    float32[3*n] positions, float32[3*n] normals, float32[2*n] uv0, [float32[2*n] uv1], [uint8[4*n] colors],
    int32 submeshCount, per submesh: int32 slot, int32 indexCount, uint32[indexCount] indices
"""
import bpy
import json
import os
import re
import struct
import sys
import time

import numpy as np

MAGIC = b"VRMESH01"
TEXTURE_INPUT_HINTS = ("Diffuse", "Albedo", "Base Color", "MRA", "Normal", "Emissive", "Mask")


def parse_args():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    out = None
    for i, a in enumerate(argv):
        if a == "--out":
            out = argv[i + 1]
    if not out:
        raise SystemExit("usage: blender -b map.blend --python export_map.py -- --out <folder>")
    return os.path.abspath(out)


def safe_name(name):
    return re.sub(r'[^A-Za-z0-9_.-]', '_', name)


def socket_value(sock):
    v = getattr(sock, "default_value", None)
    if v is None:
        return None
    try:
        return [round(float(x), 5) for x in v]
    except TypeError:
        return round(float(v), 5) if isinstance(v, (int, float, bool)) else None


# ------------------------------------------------------------------------------------------ textures
class Textures:
    def __init__(self, folder):
        self.folder = folder
        self.written = {}
        os.makedirs(folder, exist_ok=True)

    def add(self, image):
        """Writes a packed (or on-disk) image once; returns its file name or None."""
        if image is None:
            return None
        if image.name in self.written:
            return self.written[image.name]
        name = safe_name(image.name)
        if not os.path.splitext(name)[1]:
            name += ".png"
        data = None
        if image.packed_file:
            data = image.packed_file.data
        else:
            path = bpy.path.abspath(image.filepath)
            if os.path.isfile(path):
                with open(path, "rb") as f:
                    data = f.read()
        if data is None:
            self.written[image.name] = None
            return None
        with open(os.path.join(self.folder, name), "wb") as f:
            f.write(data)
        self.written[image.name] = name
        return name


# ------------------------------------------------------------------------------------------ materials
def linked_image(sock):
    """The image feeding a socket, following reroutes / simple nodes upstream."""
    seen = set()
    stack = [l.from_node for l in sock.links]
    while stack:
        n = stack.pop()
        if n in seen:
            continue
        seen.add(n)
        if n.bl_idname == "ShaderNodeTexImage":
            return n.image
        for i in n.inputs:
            stack.extend(l.from_node for l in i.links)
    return None


def export_material(mat, textures):
    # lists of {name, ...} rather than dicts so Unity's JsonUtility can read them
    entry = {"name": mat.name, "shader": "", "textures": [], "params": []}
    if mat is None or not mat.use_nodes:
        return entry
    nodes = mat.node_tree.nodes
    group = next((n for n in nodes if n.bl_idname == "ShaderNodeGroup" and n.node_tree), None)
    principled = next((n for n in nodes if n.bl_idname == "ShaderNodeBsdfPrincipled"), None)
    shader_node = group or principled
    if shader_node is None:
        return entry
    entry["shader"] = group.node_tree.name if group else "Principled"
    for sock in shader_node.inputs:
        if sock.is_linked:
            if any(h.lower() in sock.name.lower() for h in TEXTURE_INPUT_HINTS):
                img = linked_image(sock)
                name = textures.add(img)
                if name:
                    entry["textures"].append({"input": sock.name, "file": name})
        else:
            v = socket_value(sock)
            if v is not None:
                entry["params"].append({"name": sock.name, "value": v if isinstance(v, list) else [v]})
    return entry


# ------------------------------------------------------------------------------------------ meshes
def mesh_arrays(me):
    me.calc_loop_triangles()
    tri_count = len(me.loop_triangles)
    if tri_count == 0:
        return None
    loops = np.empty(tri_count * 3, dtype=np.int32)
    me.loop_triangles.foreach_get("loops", loops)
    mat_idx = np.empty(tri_count, dtype=np.int32)
    me.loop_triangles.foreach_get("material_index", mat_idx)

    loop_count = len(me.loops)
    loop_vert = np.empty(loop_count, dtype=np.int32)
    me.loops.foreach_get("vertex_index", loop_vert)
    co = np.empty(len(me.vertices) * 3, dtype=np.float32)
    me.vertices.foreach_get("co", co)
    co = co.reshape(-1, 3)[loop_vert]

    nrm = np.empty(loop_count * 3, dtype=np.float32)
    me.corner_normals.foreach_get("vector", nrm)
    nrm = nrm.reshape(-1, 3)

    cols = [co, nrm]
    flags = 0
    uv_layers = list(me.uv_layers)
    for layer in uv_layers[:2]:
        uv = np.empty(loop_count * 2, dtype=np.float32)
        layer.data.foreach_get("uv", uv)
        cols.append(uv.reshape(-1, 2))
    if not uv_layers:
        cols.append(np.zeros((loop_count, 2), dtype=np.float32))
    if len(uv_layers) >= 2:
        flags |= 1

    color = me.color_attributes.active_color
    if color is not None and len(color.data) > 0:
        c = np.empty(len(color.data) * 4, dtype=np.float32)
        color.data.foreach_get("color", c)
        c = c.reshape(-1, 4)
        if color.domain == "POINT":
            c = c[loop_vert]
        if c.shape[0] == loop_count:
            cols.append(c)
            flags |= 2

    # weld identical corners back into shared vertices
    rows = np.ascontiguousarray(np.hstack(cols))
    view = rows.view(np.dtype((np.void, rows.dtype.itemsize * rows.shape[1]))).ravel()
    _, first, inverse = np.unique(view, return_index=True, return_inverse=True)
    verts = rows[first]
    tri_verts = inverse[loops].astype(np.uint32)

    submeshes = []
    for slot in np.unique(mat_idx):
        sel = np.repeat(mat_idx == slot, 3)
        submeshes.append((int(slot), tri_verts[sel]))
    return verts, flags, submeshes


def write_mesh(f, name, verts, flags, submeshes):
    n = verts.shape[0]
    nb = name.encode("utf-8")
    f.write(struct.pack("<i", len(nb)))
    f.write(nb)
    f.write(struct.pack("<ii", n, flags))
    col = 0
    f.write(np.ascontiguousarray(verts[:, col:col + 3], dtype="<f4").tobytes()); col += 3   # positions
    f.write(np.ascontiguousarray(verts[:, col:col + 3], dtype="<f4").tobytes()); col += 3   # normals
    f.write(np.ascontiguousarray(verts[:, col:col + 2], dtype="<f4").tobytes()); col += 2   # uv0
    if flags & 1:
        f.write(np.ascontiguousarray(verts[:, col:col + 2], dtype="<f4").tobytes()); col += 2
    if flags & 2:
        rgba = np.clip(np.round(verts[:, col:col + 4] * 255.0), 0, 255).astype(np.uint8)
        f.write(np.ascontiguousarray(rgba).tobytes()); col += 4
    f.write(struct.pack("<i", len(submeshes)))
    for slot, idx in submeshes:
        f.write(struct.pack("<ii", slot, idx.shape[0]))
        f.write(np.ascontiguousarray(idx, dtype="<u4").tobytes())


# ------------------------------------------------------------------------------------------ main
def main():
    out = parse_args()
    os.makedirs(out, exist_ok=True)
    t0 = time.time()
    textures = Textures(os.path.join(out, "textures"))

    objects = [o for o in bpy.data.objects if o.type == "MESH" and not o.hide_render and o.data and len(o.data.polygons) > 0]
    materials, material_index = [], {}

    def mat_id(mat):
        key = mat.name if mat else ""
        if key not in material_index:
            material_index[key] = len(materials)
            materials.append(export_material(mat, textures) if mat else {"name": "", "shader": "", "textures": [], "params": []})
        return material_index[key]

    mesh_names, mesh_index, placed = [], {}, []
    stats = {"verts": 0, "tris": 0}
    with open(os.path.join(out, "meshes.bin"), "wb") as f:
        f.write(MAGIC)
        f.write(struct.pack("<i", 0))   # patched below
        for o in objects:
            me = o.data
            if me.name not in mesh_index:
                arrays = mesh_arrays(me)
                if arrays is None:
                    mesh_index[me.name] = -1
                else:
                    verts, flags, subs = arrays
                    write_mesh(f, me.name, verts, flags, subs)
                    mesh_index[me.name] = len(mesh_names)
                    mesh_names.append(me.name)
                    stats["verts"] += verts.shape[0]
                    stats["tris"] += sum(s[1].shape[0] for s in subs) // 3
            mi = mesh_index[me.name]
            if mi < 0:
                continue
            m = o.matrix_world
            placed.append({
                "name": o.name,
                "mesh": mi,
                "collection": o.users_collection[0].name if o.users_collection else "",
                "matrix": [round(m[r][c], 6) for r in range(4) for c in range(4)],   # row-major
                "materials": [mat_id(s.material) for s in o.material_slots] or [mat_id(None)],
            })
        f.seek(len(MAGIC))
        f.write(struct.pack("<i", len(mesh_names)))

    manifest = {
        "source": bpy.data.filepath,
        "space": "blender",     # metres, Z-up, Y = -Unreal Y
        "materials": materials,
        "meshes": mesh_names,
        "objects": placed,
    }
    with open(os.path.join(out, "map.json"), "w", encoding="utf-8") as f:
        json.dump(manifest, f, separators=(",", ":"))
    print(f"EXPORTED {out}: {len(mesh_names)} meshes ({stats['verts']} verts, {stats['tris']} tris), "
          f"{len(placed)} objects, {len(materials)} materials, "
          f"{sum(1 for v in textures.written.values() if v)} textures in {time.time() - t0:.1f}s")


main()
