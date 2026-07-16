bl_info = {
    "name": "Rust Ripper",
    "author": "Rust Ripper",
    "version": (0, 2, 0),
    "blender": (4, 2, 0),
    "location": "3D Viewport > Sidebar > Rust  |  File > Import",
    "description": "Import Rust Ripper GLB exports: PBR materials, blend layers, light tools, bridge connection",
    "category": "Import-Export",
}

import json
import os
import urllib.request

import bpy
from bpy.props import BoolProperty, FloatProperty, PointerProperty, StringProperty
from bpy_extras.io_utils import ImportHelper
from mathutils import Vector

DAEMON = "http://127.0.0.1:17071"


# ---------------------------------------------------------------- settings

class RustRipperSettings(bpy.types.PropertyGroup):
    root_display_size: FloatProperty(
        name="Root Size", description="Display size of imported root empties",
        default=10.0, min=0.1, max=100.0)
    auto_hide: BoolProperty(
        name="Hide utility objects",
        description="Hide objects the game never shows (disabled renderers, IO origins, runtime flares)",
        default=True)
    reuse_meshes: BoolProperty(
        name="Reuse existing meshes",
        description="If a mesh with the same Unity identity is already in the file, link it instead of importing a duplicate (e.g. snow and normal pines share one mesh)",
        default=True)


# ---------------------------------------------------------------- core

def _mesh_key(mesh):
    pid = mesh.get("unity_path_id")
    return (str(pid), str(mesh.get("unity_collection"))) if pid is not None else None


def _post_process(objects, settings):
    """Apply everything the GLB carries but core glTF cannot express."""
    hidden = 0
    reused = 0
    new_meshes = {o.data for o in objects if o.type == "MESH"}
    registry = {}
    if settings.reuse_meshes:
        for mesh in bpy.data.meshes:
            key = _mesh_key(mesh)
            if key and mesh not in new_meshes and key not in registry:
                registry[key] = mesh
    for obj in objects:
        if settings.auto_hide and obj.get("unity_hidden"):
            obj.hide_set(True)
            obj.hide_render = True
            hidden += 1
        if obj.parent is None and obj.type == "EMPTY":
            obj.empty_display_size = settings.root_display_size
        if obj.type == "MESH" and settings.reuse_meshes:
            key = _mesh_key(obj.data)
            if key and key in registry and registry[key] is not obj.data:
                duplicate = obj.data
                obj.data = registry[key]
                if duplicate.users == 0:
                    bpy.data.meshes.remove(duplicate)
                reused += 1
            elif key:
                registry[key] = obj.data
        if obj.type == "LIGHT":
            info = _light_info(obj)
            if info:
                # glTF has no range: use Blender's custom distance cutoff so
                # small intense lights (gauges) stop flooding the scene
                rng = info.get("unity_range", 0)
                if rng and rng > 0:
                    obj.data.use_custom_distance = True
                    obj.data.cutoff_distance = rng
                if hasattr(obj.data, "shadow_soft_size"):
                    obj.data.shadow_soft_size = max(obj.data.shadow_soft_size, 0.03)
    return hidden, reused


def _build_paint_nodes(glb_path, materials):
    """Layer-nodes exports ship the albedo raw plus mask sidecars: build
    mask -> Mix.Factor, albedo -> A, _RUST_DETAILCOLOR attribute -> B."""
    base = os.path.splitext(glb_path)[0]
    built = 0
    for mat in materials:
        if not mat or not mat.use_nodes:
            continue
        mask_path = f"{base}.detailmask.{mat.name}.png"
        if not os.path.exists(mask_path):
            continue
        tree = mat.node_tree
        bsdf = next((n for n in tree.nodes if n.type == "BSDF_PRINCIPLED"), None)
        if not bsdf or not bsdf.inputs["Base Color"].is_linked:
            continue
        albedo_out = bsdf.inputs["Base Color"].links[0].from_socket

        mask_img = bpy.data.images.load(mask_path, check_existing=True)
        mask_img.colorspace_settings.name = "Non-Color"
        mask_node = tree.nodes.new("ShaderNodeTexImage")
        mask_node.image = mask_img
        mask_node.label = "Paint Mask"
        mask_node.location = (-600, 500)

        attr = tree.nodes.new("ShaderNodeVertexColor")
        attr.layer_name = "_RUST_DETAILCOLOR"
        attr.label = "Detail Colour"
        attr.location = (-600, 250)

        mix = tree.nodes.new("ShaderNodeMix")
        mix.data_type = "RGBA"
        mix.blend_type = "MULTIPLY"
        mix.label = "Paint"
        mix.location = (-300, 400)

        tree.links.new(mask_node.outputs["Color"], mix.inputs["Factor"])
        tree.links.new(albedo_out, mix.inputs[6])
        tree.links.new(attr.outputs["Color"], mix.inputs[7])
        tree.links.new(mix.outputs[2], bsdf.inputs["Base Color"])
        built += 1
    return built


# ------------------------------------------------- blend layer (compiled-shader curve)

def _texture_entry(mat, slot):
    """unity_textures extras entry {name, scale, offset} for a slot, or None."""
    textures = mat.get("unity_textures")
    entry = textures.get(slot) if textures is not None and hasattr(textures, "get") else None
    return dict(entry) if entry is not None and hasattr(entry, "keys") else None


def _sidecar_path(glb_path, texture_name):
    path = f"{os.path.splitext(glb_path)[0]}.{texture_name}.png"
    return path if os.path.exists(path) else None


def _material_color_attributes(mat, objects):
    """Colour attribute names present on the imported meshes using this material."""
    names = set()
    for obj in objects:
        if obj.type == "MESH" and any(slot.material is mat for slot in obj.material_slots):
            names |= {a.name for a in obj.data.color_attributes}
    return names


_BLEND_LAYER_GROUP = "Rust/Standard Blend Layer"
_GROUP_VERSION = 2


def _blend_layer_group():
    """One shared node group per shader family - the Blender equivalent of
    the shader asset itself. Materials are instances: their own textures
    outside, their own authored values on the group's sliders. The math
    inside is the exact curve read from the game's compiled fragment
    programs (docs/OUTPUT_CONTRACT.md):

        blend = min(1, (vertexWeight * tintAlpha * mask.G * (_DetailBlendFactor + 1)) ** _DetailBlendFalloff)
        color = lerp(base, detailAlbedo * tint, blend)

    Versioned: an older group in the file gets missing sockets added and
    its internals rebuilt in place, so existing materials keep working.
    """
    group = bpy.data.node_groups.get(_BLEND_LAYER_GROUP)
    if group is not None and group.get("rust_ripper_version", 0) >= _GROUP_VERSION:
        return group
    if group is None:
        group = bpy.data.node_groups.new(_BLEND_LAYER_GROUP, "ShaderNodeTree")
    group["rust_ripper_version"] = _GROUP_VERSION
    group.use_fake_user = True

    present = {(item.name, item.in_out) for item in group.interface.items_tree
               if getattr(item, "in_out", None)}

    def socket(name, in_out, socket_type, default=None):
        if (name, in_out) in present:
            return
        item = group.interface.new_socket(name=name, in_out=in_out, socket_type=socket_type)
        if default is not None:
            item.default_value = default

    socket("Base Color", "INPUT", "NodeSocketColor", (0.8, 0.8, 0.8, 1.0))
    socket("Detail Albedo", "INPUT", "NodeSocketColor", (1.0, 1.0, 1.0, 1.0))
    socket("Tint", "INPUT", "NodeSocketColor", (1.0, 1.0, 1.0, 1.0))
    socket("Tint Alpha", "INPUT", "NodeSocketFloat", 1.0)
    socket("Mask", "INPUT", "NodeSocketColor", (1.0, 1.0, 1.0, 1.0))
    socket("Vertex Weight", "INPUT", "NodeSocketFloat", 1.0)
    # shader defaults; each material instance sets its authored values
    socket("_DetailBlendFactor", "INPUT", "NodeSocketFloat", 8.0)
    socket("_DetailBlendFalloff", "INPUT", "NodeSocketFloat", 1.0)
    socket("_DetailBlendMaskMapInvert", "INPUT", "NodeSocketFloat", 0.0)
    socket("Color", "OUTPUT", "NodeSocketColor")
    socket("Blend Factor", "OUTPUT", "NodeSocketFloat")

    nodes, links = group.nodes, group.links
    nodes.clear()
    group_in = nodes.new("NodeGroupInput")
    group_out = nodes.new("NodeGroupOutput")

    def math(op, label, first=None, second=None):
        node = nodes.new("ShaderNodeMath")
        node.operation = op
        node.label = label
        if first is not None:
            node.inputs[0].default_value = first
        if second is not None:
            node.inputs[1].default_value = second
        return node

    sep = nodes.new("ShaderNodeSeparateColor")
    sep.label = "mask green (shader reads .g)"
    links.new(group_in.outputs["Mask"], sep.inputs["Color"])

    inverted = math("SUBTRACT", "1 - mask", first=1.0)
    links.new(sep.outputs["Green"], inverted.inputs[1])
    pick = nodes.new("ShaderNodeMix")
    pick.data_type = "FLOAT"
    pick.label = "_DetailBlendMaskMapInvert switch"
    links.new(group_in.outputs["_DetailBlendMaskMapInvert"], pick.inputs[0])
    links.new(sep.outputs["Green"], pick.inputs[2])
    links.new(inverted.outputs[0], pick.inputs[3])

    weighted = math("MULTIPLY", "mask x vertex weight")
    links.new(pick.outputs[0], weighted.inputs[0])
    links.new(group_in.outputs["Vertex Weight"], weighted.inputs[1])

    tint_alpha = math("MULTIPLY", "x tint alpha (weight = vcol.a x tint.a)")
    links.new(weighted.outputs[0], tint_alpha.inputs[0])
    links.new(group_in.outputs["Tint Alpha"], tint_alpha.inputs[1])

    gain = math("ADD", "_DetailBlendFactor + 1", second=1.0)
    links.new(group_in.outputs["_DetailBlendFactor"], gain.inputs[0])
    gained = math("MULTIPLY", "x (_DetailBlendFactor + 1)")
    links.new(tint_alpha.outputs[0], gained.inputs[0])
    links.new(gain.outputs[0], gained.inputs[1])

    curved = math("POWER", "^ _DetailBlendFalloff")
    links.new(gained.outputs[0], curved.inputs[0])
    links.new(group_in.outputs["_DetailBlendFalloff"], curved.inputs[1])
    clamped = math("MINIMUM", "min 1 (saturate)", second=1.0)
    links.new(curved.outputs[0], clamped.inputs[0])

    tinted = nodes.new("ShaderNodeMix")
    tinted.data_type = "RGBA"
    tinted.blend_type = "MULTIPLY"
    tinted.inputs[0].default_value = 1.0
    tinted.label = "detail layer (albedo x colour)"
    links.new(group_in.outputs["Detail Albedo"], tinted.inputs[6])
    links.new(group_in.outputs["Tint"], tinted.inputs[7])

    blend = nodes.new("ShaderNodeMix")
    blend.data_type = "RGBA"
    blend.blend_type = "MIX"
    blend.label = "blend by _DetailBlendMaskMap"
    links.new(clamped.outputs[0], blend.inputs[0])
    links.new(group_in.outputs["Base Color"], blend.inputs[6])
    links.new(tinted.outputs[2], blend.inputs[7])

    links.new(blend.outputs[2], group_out.inputs["Color"])
    links.new(clamped.outputs[0], group_out.inputs["Blend Factor"])
    _arrange_nodes(group)
    return group


def _build_blend_layer_nodes(glb_path, materials, objects):
    """Materials with an active blend layer get a group instance wired to
    their own textures and attributes - samplers cannot pass through group
    sockets, so they live in the material and feed sampled colors in."""
    built = 0
    for mat in materials:
        if not mat or not mat.use_nodes:
            continue
        floats = mat.get("unity_floats")
        if floats is None or floats.get("_DetailBlendLayer", 0.0) != 1.0:
            continue
        mask_entry = _texture_entry(mat, "_DetailBlendMaskMap")
        detail_entry = _texture_entry(mat, "_DetailAlbedoMap")
        mask_path = mask_entry and _sidecar_path(glb_path, mask_entry["name"])
        detail_path = detail_entry and _sidecar_path(glb_path, detail_entry["name"])
        if not mask_path or not detail_path:
            continue
        tree = mat.node_tree
        bsdf = next((n for n in tree.nodes if n.type == "BSDF_PRINCIPLED"), None)
        if bsdf is None:
            continue
        attrs = _material_color_attributes(mat, objects)
        nodes, links = tree.nodes, tree.links

        def image_node(path, label, non_color):
            img = bpy.data.images.load(path, check_existing=True)
            if non_color:
                img.colorspace_settings.name = "Non-Color"
            node = nodes.new("ShaderNodeTexImage")
            node.image = img
            node.label = label
            return node

        layer = nodes.new("ShaderNodeGroup")
        layer.node_tree = _blend_layer_group()
        layer.label = _BLEND_LAYER_GROUP
        # authored values, straight from the material data
        layer.inputs["_DetailBlendFactor"].default_value = floats.get("_DetailBlendFactor", 8.0)
        layer.inputs["_DetailBlendFalloff"].default_value = floats.get("_DetailBlendFalloff", 1.0)
        layer.inputs["_DetailBlendMaskMapInvert"].default_value = floats.get("_DetailBlendMaskMapInvert", 0.0)

        mask_node = image_node(mask_path, "_DetailBlendMaskMap", non_color=True)
        if floats.get("_DetailBlendMaskAddLowFreq", 0.0) != 0.0:
            mask_node.label += " (AddLowFreq second sample not built)"
        links.new(mask_node.outputs["Color"], layer.inputs["Mask"])

        # meshes without a colour stream read (1,1,1,1) in Unity: leave weight at 1
        if "_RUST_COLOR" in attrs:
            vcol = nodes.new("ShaderNodeVertexColor")
            vcol.layer_name = "_RUST_COLOR"
            vcol.label = "vertex colour (blend weight)"
            if floats.get("_DetailBlendMaskVertexSource", 0.0) == 0.0:
                links.new(vcol.outputs["Alpha"], layer.inputs["Vertex Weight"])
            else:
                vsep = nodes.new("ShaderNodeSeparateColor")
                vsep.label = "_DetailBlendMaskVertexSource=1 (red)"
                links.new(vcol.outputs["Color"], vsep.inputs["Color"])
                links.new(vsep.outputs["Red"], layer.inputs["Vertex Weight"])

        detail_node = image_node(detail_path, "_DetailAlbedoMap", non_color=False)
        scale = list(detail_entry.get("scale", [1.0, 1.0]))
        offset = list(detail_entry.get("offset", [0.0, 0.0]))
        if scale != [1.0, 1.0] or offset != [0.0, 0.0]:
            mapping = nodes.new("ShaderNodeMapping")
            mapping.label = "_DetailAlbedoMap tiling (data)"
            mapping.inputs["Scale"].default_value = (scale[0], scale[1], 1.0)
            mapping.inputs["Location"].default_value = (offset[0], offset[1], 0.0)
            uv = nodes.new("ShaderNodeUVMap")
            links.new(uv.outputs["UV"], mapping.inputs["Vector"])
            links.new(mapping.outputs["Vector"], detail_node.inputs["Vector"])
        links.new(detail_node.outputs["Color"], layer.inputs["Detail Albedo"])

        # weight = vcol.a x tint.a in the compiled shader: alpha rides along
        if "_RUST_CUSTOMCOLOUR_01" in attrs:
            tint = nodes.new("ShaderNodeVertexColor")
            tint.layer_name = "_RUST_CUSTOMCOLOUR_01"
            tint.label = "customColour 01 (swap layer for other palette entries)"
            links.new(tint.outputs["Color"], layer.inputs["Tint"])
            links.new(tint.outputs["Alpha"], layer.inputs["Tint Alpha"])
        elif "_RUST_DETAILCOLOR" in attrs:
            tint = nodes.new("ShaderNodeVertexColor")
            tint.layer_name = "_RUST_DETAILCOLOR"
            tint.label = "_DetailColor (authored)"
            links.new(tint.outputs["Color"], layer.inputs["Tint"])
            links.new(tint.outputs["Alpha"], layer.inputs["Tint Alpha"])
        else:
            colors = mat.get("unity_colors")
            authored = list(colors.get("_DetailColor", [1.0, 1.0, 1.0, 1.0])) if colors is not None else [1.0, 1.0, 1.0, 1.0]
            layer.inputs["Tint"].default_value = (*authored[:3], 1.0)
            layer.inputs["Tint Alpha"].default_value = authored[3] if len(authored) > 3 else 1.0

        base_input = bsdf.inputs["Base Color"]
        if base_input.is_linked:
            links.new(base_input.links[0].from_socket, layer.inputs["Base Color"])
        else:
            layer.inputs["Base Color"].default_value = base_input.default_value
        links.new(layer.outputs["Color"], base_input)
        built += 1
    return built


_BLEND4WAY_GROUP = "Rust/Standard Blend 4-Way (layer)"


def _blend4way_group():
    """One application of a numbered blend layer, chainable Color->Color.
    Curve read from the compiled 4-Way fragment programs - identical to the
    Blend Layer curve; weights come from vertex COLOR r/g/b per layer:

        blend = min(1, (weight * mask.G * (_BlendFactor + 1)) ** _BlendFalloff)
        layer = _AlbedoTintMask ? lerp(albedo, albedo * color, albedo.a)
                                : albedo * color
        out   = lerp(base, layer, blend)
    """
    group = bpy.data.node_groups.get(_BLEND4WAY_GROUP)
    if group is not None and group.get("rust_ripper_version", 0) >= 1:
        return group
    if group is None:
        group = bpy.data.node_groups.new(_BLEND4WAY_GROUP, "ShaderNodeTree")
    group["rust_ripper_version"] = 1
    group.use_fake_user = True

    present = {(item.name, item.in_out) for item in group.interface.items_tree
               if getattr(item, "in_out", None)}

    def socket(name, in_out, socket_type, default=None):
        if (name, in_out) in present:
            return
        item = group.interface.new_socket(name=name, in_out=in_out, socket_type=socket_type)
        if default is not None:
            item.default_value = default

    socket("Base Color", "INPUT", "NodeSocketColor", (0.8, 0.8, 0.8, 1.0))
    socket("Layer Albedo", "INPUT", "NodeSocketColor", (1.0, 1.0, 1.0, 1.0))
    socket("Layer Albedo Alpha", "INPUT", "NodeSocketFloat", 1.0)
    socket("Layer Color", "INPUT", "NodeSocketColor", (1.0, 1.0, 1.0, 1.0))
    socket("_AlbedoTintMask", "INPUT", "NodeSocketFloat", 0.0)
    socket("Mask", "INPUT", "NodeSocketColor", (1.0, 1.0, 1.0, 1.0))
    socket("Weight", "INPUT", "NodeSocketFloat", 1.0)
    socket("_BlendFactor", "INPUT", "NodeSocketFloat", 8.0)
    socket("_BlendFalloff", "INPUT", "NodeSocketFloat", 1.0)
    socket("_BlendMaskMapInvert", "INPUT", "NodeSocketFloat", 0.0)
    socket("Color", "OUTPUT", "NodeSocketColor")
    socket("Blend Factor", "OUTPUT", "NodeSocketFloat")

    nodes, links = group.nodes, group.links
    nodes.clear()
    group_in = nodes.new("NodeGroupInput")
    group_out = nodes.new("NodeGroupOutput")

    def math(op, label, first=None, second=None):
        node = nodes.new("ShaderNodeMath")
        node.operation = op
        node.label = label
        if first is not None:
            node.inputs[0].default_value = first
        if second is not None:
            node.inputs[1].default_value = second
        return node

    def mix_rgba(blend_type, label):
        node = nodes.new("ShaderNodeMix")
        node.data_type = "RGBA"
        node.blend_type = blend_type
        node.label = label
        return node

    sep = nodes.new("ShaderNodeSeparateColor")
    sep.label = "mask green (shader reads .g)"
    links.new(group_in.outputs["Mask"], sep.inputs["Color"])

    inverted = math("SUBTRACT", "1 - mask", first=1.0)
    links.new(sep.outputs["Green"], inverted.inputs[1])
    pick = nodes.new("ShaderNodeMix")
    pick.data_type = "FLOAT"
    pick.label = "_BlendMaskMapInvert switch"
    links.new(group_in.outputs["_BlendMaskMapInvert"], pick.inputs[0])
    links.new(sep.outputs["Green"], pick.inputs[2])
    links.new(inverted.outputs[0], pick.inputs[3])

    weighted = math("MULTIPLY", "mask x vertex weight")
    links.new(pick.outputs[0], weighted.inputs[0])
    links.new(group_in.outputs["Weight"], weighted.inputs[1])

    gain = math("ADD", "_BlendFactor + 1", second=1.0)
    links.new(group_in.outputs["_BlendFactor"], gain.inputs[0])
    gained = math("MULTIPLY", "x (_BlendFactor + 1)")
    links.new(weighted.outputs[0], gained.inputs[0])
    links.new(gain.outputs[0], gained.inputs[1])
    curved = math("POWER", "^ _BlendFalloff")
    links.new(gained.outputs[0], curved.inputs[0])
    links.new(group_in.outputs["_BlendFalloff"], curved.inputs[1])
    clamped = math("MINIMUM", "min 1 (saturate)", second=1.0)
    links.new(curved.outputs[0], clamped.inputs[0])

    tint_full = mix_rgba("MULTIPLY", "albedo x _Color")
    tint_full.inputs[0].default_value = 1.0
    links.new(group_in.outputs["Layer Albedo"], tint_full.inputs[6])
    links.new(group_in.outputs["Layer Color"], tint_full.inputs[7])

    tint_masked = mix_rgba("MIX", "tint through albedo alpha")
    links.new(group_in.outputs["Layer Albedo Alpha"], tint_masked.inputs[0])
    links.new(group_in.outputs["Layer Albedo"], tint_masked.inputs[6])
    links.new(tint_full.outputs[2], tint_masked.inputs[7])

    tint_pick = mix_rgba("MIX", "_AlbedoTintMask switch")
    links.new(group_in.outputs["_AlbedoTintMask"], tint_pick.inputs[0])
    links.new(tint_full.outputs[2], tint_pick.inputs[6])
    links.new(tint_masked.outputs[2], tint_pick.inputs[7])

    blend = mix_rgba("MIX", "blend layer")
    links.new(clamped.outputs[0], blend.inputs[0])
    links.new(group_in.outputs["Base Color"], blend.inputs[6])
    links.new(tint_pick.outputs[2], blend.inputs[7])

    links.new(blend.outputs[2], group_out.inputs["Color"])
    links.new(clamped.outputs[0], group_out.inputs["Blend Factor"])
    _arrange_nodes(group)
    return group


def _build_blend4way_nodes(glb_path, materials, objects):
    """Numbered blend layers (_BlendLayer1..3): chain one group instance per
    enabled layer; weights are vertex COLOR r/g/b, all values from data."""
    built = 0
    for mat in materials:
        if not mat or not mat.use_nodes:
            continue
        floats = mat.get("unity_floats")
        if floats is None:
            continue
        layers = []
        for n in (1, 2, 3):
            if floats.get(f"_BlendLayer{n}", 0.0) != 1.0:
                continue
            albedo_entry = _texture_entry(mat, f"_BlendLayer{n}_AlbedoMap")
            mask_entry = _texture_entry(mat, f"_BlendLayer{n}_BlendMaskMap")
            albedo_path = albedo_entry and _sidecar_path(glb_path, albedo_entry["name"])
            mask_path = mask_entry and _sidecar_path(glb_path, mask_entry["name"])
            if albedo_path and mask_path:
                layers.append((n, albedo_entry, albedo_path, mask_path))
        if not layers:
            continue
        tree = mat.node_tree
        bsdf = next((n for n in tree.nodes if n.type == "BSDF_PRINCIPLED"), None)
        if bsdf is None:
            continue
        attrs = _material_color_attributes(mat, objects)
        nodes, links = tree.nodes, tree.links
        colors = mat.get("unity_colors")

        weight_sep = None
        if "_RUST_COLOR" in attrs:
            vcol = nodes.new("ShaderNodeVertexColor")
            vcol.layer_name = "_RUST_COLOR"
            vcol.label = "vertex colour (layer weights r/g/b)"
            weight_sep = nodes.new("ShaderNodeSeparateColor")
            weight_sep.label = "layer weights"
            links.new(vcol.outputs["Color"], weight_sep.inputs["Color"])

        base_input = bsdf.inputs["Base Color"]
        chain_socket = base_input.links[0].from_socket if base_input.is_linked else None
        for n, albedo_entry, albedo_path, mask_path in layers:
            layer = nodes.new("ShaderNodeGroup")
            layer.node_tree = _blend4way_group()
            layer.label = f"_BlendLayer{n}"
            layer.inputs["_BlendFactor"].default_value = floats.get(f"_BlendLayer{n}_BlendFactor", 8.0)
            layer.inputs["_BlendFalloff"].default_value = floats.get(f"_BlendLayer{n}_BlendFalloff", 1.0)
            layer.inputs["_BlendMaskMapInvert"].default_value = floats.get(f"_BlendLayer{n}_BlendMaskMapInvert", 0.0)
            layer.inputs["_AlbedoTintMask"].default_value = floats.get(f"_BlendLayer{n}_AlbedoTintMask", 0.0)
            if colors is not None:
                authored = list(colors.get(f"_BlendLayer{n}_Color", [1.0, 1.0, 1.0, 1.0]))
                layer.inputs["Layer Color"].default_value = (*[c ** 2.2 for c in authored[:3]], 1.0)

            img = bpy.data.images.load(albedo_path, check_existing=True)
            albedo_node = nodes.new("ShaderNodeTexImage")
            albedo_node.image = img
            albedo_node.label = f"_BlendLayer{n}_AlbedoMap"
            scale = list(albedo_entry.get("scale", [1.0, 1.0]))
            offset = list(albedo_entry.get("offset", [0.0, 0.0]))
            if scale != [1.0, 1.0] or offset != [0.0, 0.0]:
                mapping = nodes.new("ShaderNodeMapping")
                mapping.label = f"_BlendLayer{n} tiling (data)"
                mapping.inputs["Scale"].default_value = (scale[0], scale[1], 1.0)
                mapping.inputs["Location"].default_value = (offset[0], offset[1], 0.0)
                uv = nodes.new("ShaderNodeUVMap")
                links.new(uv.outputs["UV"], mapping.inputs["Vector"])
                links.new(mapping.outputs["Vector"], albedo_node.inputs["Vector"])
            links.new(albedo_node.outputs["Color"], layer.inputs["Layer Albedo"])
            links.new(albedo_node.outputs["Alpha"], layer.inputs["Layer Albedo Alpha"])

            mask_img = bpy.data.images.load(mask_path, check_existing=True)
            mask_img.colorspace_settings.name = "Non-Color"
            mask_node = nodes.new("ShaderNodeTexImage")
            mask_node.image = mask_img
            mask_node.label = f"_BlendLayer{n}_BlendMaskMap"
            links.new(mask_node.outputs["Color"], layer.inputs["Mask"])

            if weight_sep is not None:
                links.new(weight_sep.outputs[("Red", "Green", "Blue")[n - 1]], layer.inputs["Weight"])

            if chain_socket is not None:
                links.new(chain_socket, layer.inputs["Base Color"])
            else:
                layer.inputs["Base Color"].default_value = base_input.default_value
            chain_socket = layer.outputs["Color"]
        links.new(chain_socket, base_input)
        built += 1
    return built


_ALPHA_CLIP_GROUP = "Rust Alpha Clip"


def _alpha_clip_group():
    """One tiny shared group for alpha testing: Alpha >= _Cutoff -> 1 else 0.
    Replaces the importer's sprawling Alpha Clip frame with a single node."""
    group = bpy.data.node_groups.get(_ALPHA_CLIP_GROUP)
    if group is not None and group.get("rust_ripper_version", 0) >= 1:
        return group
    if group is None:
        group = bpy.data.node_groups.new(_ALPHA_CLIP_GROUP, "ShaderNodeTree")
    group["rust_ripper_version"] = 1
    group.use_fake_user = True
    present = {(item.name, item.in_out) for item in group.interface.items_tree
               if getattr(item, "in_out", None)}

    def socket(name, in_out, socket_type, default=None):
        if (name, in_out) in present:
            return
        item = group.interface.new_socket(name=name, in_out=in_out, socket_type=socket_type)
        if default is not None:
            item.default_value = default

    socket("Alpha", "INPUT", "NodeSocketFloat", 1.0)
    socket("_Cutoff", "INPUT", "NodeSocketFloat", 0.5)
    socket("Alpha", "OUTPUT", "NodeSocketFloat")

    nodes, links = group.nodes, group.links
    nodes.clear()
    group_in = nodes.new("NodeGroupInput")
    group_out = nodes.new("NodeGroupOutput")
    cut = nodes.new("ShaderNodeMath")
    cut.operation = "GREATER_THAN"
    cut.label = "alpha >= _Cutoff"
    links.new(group_in.outputs["Alpha"], cut.inputs[0])
    links.new(group_in.outputs["_Cutoff"], cut.inputs[1])
    links.new(cut.outputs[0], group_out.inputs["Alpha"])
    _arrange_nodes(group)
    return group


def _compact_alpha_clips(materials):
    """Swap the glTF importer's Alpha Clip frame (two chained math nodes) for
    the shared Rust Alpha Clip group; cutoff comes from the material data."""
    built = 0
    for mat in materials:
        if not mat or not mat.use_nodes:
            continue
        tree = mat.node_tree
        frame = next((n for n in tree.nodes if n.type == "FRAME" and n.label == "Alpha Clip"), None)
        bsdf = next((n for n in tree.nodes if n.type == "BSDF_PRINCIPLED"), None)
        if frame is None or bsdf is None or not bsdf.inputs["Alpha"].is_linked:
            continue
        members = [n for n in tree.nodes if n.parent is frame]
        # the chain's source: whatever feeds a member from outside the frame
        source = None
        cutoff = None
        for n in members:
            for sock in n.inputs:
                for link in sock.links:
                    if link.from_node not in members:
                        source = link.from_socket
            for sock in n.inputs:
                if not sock.is_linked and sock.type == "VALUE" and sock.default_value not in (0.0, 1.0):
                    cutoff = sock.default_value
        if source is None:
            continue
        floats = mat.get("unity_floats")
        if floats is not None and "_Cutoff" in floats.keys():
            cutoff = floats["_Cutoff"]
        clip = tree.nodes.new("ShaderNodeGroup")
        clip.node_tree = _alpha_clip_group()
        clip.label = _ALPHA_CLIP_GROUP
        if cutoff is not None:
            clip.inputs["_Cutoff"].default_value = cutoff
        tree.links.new(source, clip.inputs["Alpha"])
        tree.links.new(clip.outputs["Alpha"], bsdf.inputs["Alpha"])
        for n in members:
            tree.nodes.remove(n)
        tree.nodes.remove(frame)
        built += 1
    return built


def _count_fur_materials(materials):
    """Alpha-tested fur (AnimalFur): the glTF MASK import (raw albedo alpha,
    alphaCutoff = _Cutoff) is the right graph as-is - the full shader formula
    additionally lerps toward vertex red (docs/OUTPUT_CONTRACT.md) but the
    plain clip reads correctly. Detected by the mechanism's own parameters,
    only to raise the Cycles transparent bounce budget for the shell stack."""
    count = 0
    for mat in materials:
        if not mat or not mat.use_nodes:
            continue
        floats = mat.get("unity_floats")
        if floats is not None and all(k in floats.keys() for k in ("_AlphaLerp", "_AlphaNudge", "_Cutoff")):
            count += 1
    return count


# ------------------------------------------------------------- node layout

_NODE_HEIGHT = {
    "TEX_IMAGE": 290, "BSDF_PRINCIPLED": 640, "MIX": 190, "MATH": 160,
    "VALUE": 90, "RGB": 200, "SEPARATE_COLOR": 130, "NORMAL_MAP": 170,
    "MAPPING": 330, "UVMAP": 100, "VERTEX_COLOR": 120, "GROUP": 300,
    "GROUP_INPUT": 300, "GROUP_OUTPUT": 120,
    "OUTPUT_MATERIAL": 110,
}


def _arrange_nodes(tree):
    """Neat left-to-right layout. Columns = longest link distance to the
    output. Rows follow the consumers' input-socket order (base colour
    chain is the top band, roughness below, normal below that - the BSDF's
    own order), and every node is pulled level with the socket it feeds,
    pushed down only when it would overlap its column neighbour."""
    nodes = [n for n in tree.nodes if n.type != "FRAME"]
    depth = {n: 0 for n in nodes}
    for _ in range(len(nodes)):
        changed = False
        for link in tree.links:
            a, b = link.from_node, link.to_node
            if a in depth and b in depth and depth[a] < depth[b] + 1:
                depth[a] = depth[b] + 1
                changed = True
        if not changed:
            break

    # semantic order: depth-first from the output side, inputs top-to-bottom,
    # so each column stacks in the order its chains hang off the BSDF
    order = {}

    def visit(node):
        if node in order:
            return
        order[node] = len(order)
        for socket in node.inputs:
            for link in socket.links:
                visit(link.from_node)

    for node in nodes:
        if not any(s.links for s in node.outputs):
            visit(node)
    for node in nodes:
        visit(node)

    columns = {}
    for node in nodes:
        columns.setdefault(depth[node], []).append(node)
    for d in sorted(columns):
        cursor = None
        for node in sorted(columns[d], key=lambda n: order[n]):
            # level with the topmost socket this node feeds
            desired = []
            for socket in node.outputs:
                for link in socket.links:
                    consumer = link.to_node
                    try:
                        slot = list(consumer.inputs).index(link.to_socket)
                    except ValueError:
                        slot = 0
                    desired.append(consumer.location.y - 40.0 - slot * 22.0)
            y = max(desired) if desired else 0.0
            if cursor is not None:
                y = min(y, cursor)
            node.location = (-d * 340.0, y)
            cursor = y - _NODE_HEIGHT.get(node.type, 160) - 40.0


def _tidy_armatures(context, objects):
    """Make imported skeletons read like rigs: glTF joints have no length, so
    Blender draws stubs. Purely topological - each bone's tail goes to its
    single child's head (average for forks, parent direction for leaves)."""
    for arm_obj in [o for o in objects if o.type == "ARMATURE"]:
        prev_active = context.view_layer.objects.active
        try:
            bpy.ops.object.select_all(action="DESELECT")
        except RuntimeError:
            pass
        arm_obj.select_set(True)
        context.view_layer.objects.active = arm_obj
        try:
            bpy.ops.object.mode_set(mode="EDIT")
            bones = arm_obj.data.edit_bones
            for bone in bones:
                children = bone.children
                if len(children) == 1:
                    target = children[0].head
                elif len(children) > 1:
                    target = sum((c.head for c in children), Vector()) / len(children)
                elif bone.parent is not None:
                    direction = (bone.head - bone.parent.head)
                    target = bone.head + (direction.normalized() * max(bone.parent.length * 0.5, 0.02)
                                          if direction.length > 1e-6 else Vector((0, 0.05, 0)))
                else:
                    target = bone.head + Vector((0, 0.1, 0))
                if (target - bone.head).length > 1e-4:
                    bone.tail = target
            for bone in bones:
                if bone.parent is not None and (bone.head - bone.parent.tail).length < 1e-5:
                    bone.use_connect = True
            bpy.ops.object.mode_set(mode="OBJECT")
        except Exception:
            try:
                bpy.ops.object.mode_set(mode="OBJECT")
            except Exception:
                pass
        finally:
            context.view_layer.objects.active = prev_active
        arm_obj.data.display_type = "OCTAHEDRAL"
        arm_obj.show_in_front = True


def _action_bone_names(action):
    """Bone names an action animates (Blender 5 layered-action API)."""
    names = set()
    try:
        for layer in action.layers:
            for strip in layer.strips:
                for slot in action.slots:
                    bag = strip.channelbag(slot)
                    if bag:
                        for fc in bag.fcurves:
                            if 'pose.bones["' in fc.data_path:
                                names.add(fc.data_path.split('"')[1])
    except Exception:
        pass
    return names


def _sequence_clips(objects, actions, prefix):
    """Lay each armature's clips end-to-end on one NLA track: the whole
    library is visible on the timeline in sequence, and pruning a clip is
    deleting its strip - no sifting through the global action list. The
    importer's own one-strip-per-clip stash tracks are folded away."""
    armatures = [o for o in objects if o.type == "ARMATURE"]
    for arm in armatures:
        own = []
        bone_names = {b.name for b in arm.data.bones}
        for action in actions:
            animated = _action_bone_names(action)
            if animated and animated <= bone_names:
                own.append(action)
        if not own:
            continue
        if prefix and prefix.lower() not in arm.name.lower():
            arm.name = f"{prefix}.rig"
        arm.animation_data_create()
        if arm.animation_data.nla_tracks.get("Rust Clips"):
            continue
        own_set = set(own)
        for track in list(arm.animation_data.nla_tracks):
            if track.strips and all(s.action in own_set for s in track.strips):
                arm.animation_data.nla_tracks.remove(track)
        track = arm.animation_data.nla_tracks.new()
        track.name = "Rust Clips"
        frame = 1
        for action in sorted(own, key=lambda a: a.name):
            length = max(1, int(action.frame_range[1] - action.frame_range[0]) + 1)
            strip = track.strips.new(action.name, frame, action)
            strip.extrapolation = "NOTHING"
            frame += length + 10
        # keep the active action empty so the sequence is what plays
        arm.animation_data.action = None


def _import_glb(context, filepath):
    settings = context.scene.rust_ripper
    before_objects = set(bpy.data.objects)
    before_materials = set(bpy.data.materials)
    before_actions = set(bpy.data.actions)
    bpy.ops.import_scene.gltf(filepath=filepath)
    new_objects = [o for o in bpy.data.objects if o not in before_objects]
    new_materials = [m for m in bpy.data.materials if m not in before_materials]
    new_actions = [a for a in bpy.data.actions if a not in before_actions]
    hidden, reused = _post_process(new_objects, settings)
    _tidy_armatures(context, new_objects)
    # namespace actions per import so every rig's clips are findable in the
    # global action list ("wolf2|wolf_run", "chicken|walk")
    # the export root carries unity_prefab_path (contract) - the reliable name
    root = next((o for o in new_objects if o.get("unity_prefab_path")), None) \
        or next((o for o in new_objects if o.parent is None), None)
    prefix = root.name.split(".")[0] if root is not None else ""
    if prefix:
        for action in new_actions:
            if "|" not in action.name:
                action.name = f"{prefix}|{action.name}"
    _sequence_clips(new_objects, new_actions, prefix)
    # animation-target empties are load-bearing (deleting them breaks clips):
    # keep them visible but small
    for obj in new_objects:
        if obj.type == "EMPTY" and obj.get("unity_animated"):
            obj.empty_display_size = 0.05
    painted = _build_paint_nodes(filepath, new_materials)
    painted += _build_blend_layer_nodes(filepath, new_materials, new_objects)
    painted += _build_blend4way_nodes(filepath, new_materials, new_objects)
    _compact_alpha_clips(new_materials)
    if _count_fur_materials(new_materials) and context.scene.render.engine == "CYCLES":
        # fur shells stack many alpha layers; rays that exhaust Cycles'
        # transparent bounce budget terminate BLACK between the tufts
        cycles = context.scene.cycles
        cycles.transparent_max_bounces = max(cycles.transparent_max_bounces, 64)
    # NOTE: nodes should always be created neatly. The current _arrange_nodes is
    # a basic left-to-right layout. Research better programmatic shader layout
    # methods (e.g. graphviz-style Sugiyama, or Blender's node.dimensions-based
    # grid packing) for more readable auto-generated materials.
    for mat in new_materials:
        if mat.use_nodes:
            _arrange_nodes(mat.node_tree)
    return len(new_objects), hidden, painted, reused


def _light_info(obj):
    info = obj.get("unity_light")
    return dict(info) if info is not None and hasattr(info, "keys") else None


def _is_fill_light(obj):
    """Heuristic, not ground truth: vertex-mode lights are Rust's cheap fill
    lights; shadowless cookie-less faint lights are usually bounce fakes."""
    info = _light_info(obj)
    if not info:
        return False
    if info.get("unity_render_mode") == 2:
        return True
    return (info.get("unity_shadows", 0) == 0
            and not info.get("unity_cookie")
            and info.get("unity_intensity", 0) <= 2.0)


# ---------------------------------------------------------------- operators

class RUST_OT_import_glb(bpy.types.Operator, ImportHelper):
    bl_idname = "rust.import_glb"
    bl_label = "Rust Ripper GLB (.glb)"
    bl_description = "Import a Rust Ripper GLB with PBR materials, blend layers, visibility and light handling"
    bl_options = {"REGISTER", "UNDO"}
    filename_ext = ".glb"
    filter_glob: StringProperty(default="*.glb", options={"HIDDEN"})

    def execute(self, context):
        count, hidden, painted, reused = _import_glb(context, self.filepath)
        self.report({"INFO"}, f"{count} objects ({hidden} hidden, {painted} paint, {reused} meshes reused)")
        return {"FINISHED"}


class RUST_OT_check_connection(bpy.types.Operator):
    bl_idname = "rust.check_connection"
    bl_label = "Connect to Bridge"
    bl_description = "Connect to the Rust Ripper daemon bridge"

    def execute(self, context):
        try:
            with urllib.request.urlopen(f"{DAEMON}/status", timeout=5) as response:
                data = json.loads(response.read())
            context.scene.rust_ripper["bridge_connected"] = True
            self.report({"INFO"}, f"Connected — Rust Ripper {data.get('version', '?')}")
        except Exception as e:
            context.scene.rust_ripper["bridge_connected"] = False
            self.report({"WARNING"}, f"Bridge not reachable ({e})")
        return {"FINISHED"}


class RUST_OT_disconnect_bridge(bpy.types.Operator):
    bl_idname = "rust.disconnect_bridge"
    bl_label = "Disconnect Bridge"
    bl_description = "Disconnect from the Rust Ripper bridge"

    def execute(self, context):
        context.scene.rust_ripper["bridge_connected"] = False
        self.report({"INFO"}, "Bridge disconnected")
        return {"FINISHED"}


class RUST_OT_hide_fill_lights(bpy.types.Operator):
    bl_idname = "rust.hide_fill_lights"
    bl_label = "Hide Fill Lights"
    bl_description = (
        "Hide bounce/fill lights (vertex render mode, or shadowless cookie-less faint lights).\n"
        "Light types in Rust:\n"
        "  • Key lights — cast shadows, main scene lighting\n"
        "  • Fill lights — no shadows, low intensity, ambient bounce fakes\n"
        "  • Cookie lights — projected texture (stained glass, caustics)\n"
        "This operator hides fill lights only."
    )

    def execute(self, context):
        count = 0
        for obj in context.scene.objects:
            if obj.type == "LIGHT" and _is_fill_light(obj):
                obj.hide_set(True)
                obj.hide_render = True
                count += 1
        self.report({"INFO"}, f"{count} fill lights hidden")
        return {"FINISHED"}


class RUST_OT_show_all_lights(bpy.types.Operator):
    bl_idname = "rust.show_all_lights"
    bl_label = "Show All Lights"
    bl_description = "Unhide all lights in the scene"

    def execute(self, context):
        for obj in context.scene.objects:
            if obj.type == "LIGHT":
                obj.hide_set(False)
                obj.hide_render = False
        return {"FINISHED"}


# ---------------------------------------------------------------- panels

class RUST_PT_main(bpy.types.Panel):
    bl_label = "Rust Ripper"
    bl_space_type = "VIEW_3D"
    bl_region_type = "UI"
    bl_category = "Rust"

    def draw(self, context):
        layout = self.layout
        settings = context.scene.rust_ripper

        # bridge status — top row
        connected = settings.get("bridge_connected", False)
        row = layout.row(align=True)
        if connected:
            row.operator("rust.disconnect_bridge", text="Disconnect", icon="UNLINKED")
            row.label(text="Bridge active", icon="CHECKBOX_HLT")
        else:
            row.operator("rust.check_connection", text="Connect", icon="LINKED")
            row.label(text="No bridge", icon="CHECKBOX_DEHLT")

        layout.separator()
        layout.prop(settings, "root_display_size")
        layout.prop(settings, "auto_hide")
        layout.prop(settings, "reuse_meshes")
        layout.separator()
        row = layout.row(align=True)
        row.operator("rust.hide_fill_lights", icon="LIGHT_SUN")
        row.operator("rust.show_all_lights", icon="HIDE_OFF")


# ---------------------------------------------------------------- menu hook

def _apply_outliner_viewport_column(hide):
    """Hide the viewport restrict column (monitor icon) in all outliners,
    keeping the hide column (eye icon) visible."""
    for window in bpy.context.window_manager.windows:
        for area in window.screen.areas:
            if area.type == "OUTLINER":
                for space in area.spaces:
                    if space.type == "OUTLINER":
                        space.show_restrict_column_viewport = not hide


def _menu_import(self, context):
    self.layout.operator("rust.import_glb", text="Rust Ripper GLB (.glb)")


classes = (
    RustRipperSettings,
    RUST_OT_import_glb,
    RUST_OT_check_connection,
    RUST_OT_disconnect_bridge,
    RUST_OT_hide_fill_lights,
    RUST_OT_show_all_lights,
    RUST_PT_main,
)


def register():
    for cls in classes:
        bpy.utils.register_class(cls)
    bpy.types.Scene.rust_ripper = PointerProperty(type=RustRipperSettings)
    bpy.types.TOPBAR_MT_file_import.append(_menu_import)
    _apply_outliner_viewport_column(True)


def unregister():
    bpy.types.TOPBAR_MT_file_import.remove(_menu_import)
    del bpy.types.Scene.rust_ripper
    for cls in reversed(classes):
        bpy.utils.unregister_class(cls)
    _apply_outliner_viewport_column(False)


if __name__ == "__main__":
    register()
