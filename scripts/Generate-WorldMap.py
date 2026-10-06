"""Generate the shared SVG viewport and land-verified lights from Natural Earth land GeoJSON.
Usage: python scripts/Generate-WorldMap.py path/to/ne_110m_land.geojson
Source: https://github.com/nvkelso/natural-earth-vector (public domain).
"""
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
ASSETS = ROOT / "src/DiTunnel.App/Assets"
data = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
polygons = []
for feature in data["features"]:
    geometry = feature["geometry"]
    polygons.extend([geometry["coordinates"]] if geometry["type"] == "Polygon" else geometry["coordinates"])

def inside_ring(x, y, ring):
    inside = False
    for a, b in zip(ring, ring[1:] + ring[:1]):
        if (a[1] > y) != (b[1] > y) and x < (b[0] - a[0]) * (y - a[1]) / (b[1] - a[1]) + a[0]:
            inside = not inside
    return inside

def project(lon, lat):
    return round((lon + 180) * 1600 / 360, 2), round((90 - lat) * 760 / 180, 2)

cities = [
    ("Denver", -104.99, 39.74), ("Winnipeg", -97.14, 49.9), ("Mexico City", -99.13, 19.43),
    ("Bogota", -74.08, 4.61), ("Cusco", -71.97, -13.52), ("Brasilia", -47.88, -15.79),
    ("Cordoba", -64.18, -31.42), ("Birmingham", -1.9, 52.49), ("Paris", 2.35, 48.86),
    ("Madrid", -3.7, 40.42), ("Berlin", 13.4, 52.52), ("Warsaw", 21.01, 52.23),
    ("Moscow", 37.62, 55.75), ("Cairo", 31.24, 30.04), ("Kinshasa", 15.27, -4.32),
    ("Johannesburg", 28.05, -26.2), ("Delhi", 77.21, 28.61), ("Beijing", 116.4, 39.9),
    ("Novosibirsk", 82.94, 55.04), ("Alice Springs", 133.88, -23.7),
]
for name, lon, lat in cities:
    assert any(inside_ring(lon, lat, rings[0]) and not any(inside_ring(lon, lat, hole) for hole in rings[1:])
               for rings in polygons), f"Light outside land: {name}"
paths = []
for rings in polygons:
    for ring in rings:
        projected = [project(*coordinate[:2]) for coordinate in ring]
        paths.append("M" + " L".join(f"{x},{y}" for x, y in projected) + " Z")
path = " ".join(paths)
ASSETS.joinpath("world-map.svg").write_text(
    '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1600 760">\n'
    '  <title>Natural Earth land, 1:110m</title>\n'
    '  <desc>Public domain. https://www.naturalearthdata.com/ . Equirectangular projection; shared viewport with lights.</desc>\n'
    f'  <path fill="#8f7cff" fill-opacity=".14" fill-rule="evenodd" d="{path}"/>\n</svg>\n', encoding="utf-8")
positions = []
for index, (name, lon, lat) in enumerate(cities, 1):
    x, y = project(lon, lat)
    positions.append((x, y))
    ASSETS.joinpath(f"world-map-light-{index:02}.svg").write_text(
        f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1600 760"><title>{name}</title>'
        f'<g fill="#66e491" transform="translate({x} {y})"><circle r="8" opacity=".24"/><circle r="2"/></g></svg>\n', encoding="utf-8")
vm = ROOT / "src/DiTunnel.App/ViewModels/MainViewModel.cs"
content = vm.read_text(encoding="utf-8")
points = ",\n        ".join(f"new({x}, {y})" for x, y in positions)
content = re.sub(r"(// Coordinates match the twenty individual SVG layers in Assets\.\n).*?(\n    \];)",
                 lambda match: match[1] + "        " + points + match[2], content, flags=re.S)
vm.write_text(content, encoding="utf-8")
print(f"Generated {len(polygons)} land polygons and {len(positions)} lights; all light centres are on land.")
