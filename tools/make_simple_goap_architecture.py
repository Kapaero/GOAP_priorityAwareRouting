from pathlib import Path
import html
import textwrap


OUT_DIR = Path(r"C:\Users\Глеб\Documents\GOAP\analysis_outputs\goap_architecture_simple")


COLORS = {
    "agent": "#2F6FA3",
    "planner": "#4E79A7",
    "world": "#4F9D69",
    "environment": "#C85A54",
    "resource": "#D98C28",
    "metric": "#7A5DA8",
    "ink": "#1F2937",
    "muted": "#64748B",
    "line": "#334155",
}


def wrap(text, width):
    lines = []
    for paragraph in text.split("\n"):
        lines.extend(textwrap.wrap(paragraph, width=width))
    return lines


class Svg:
    def __init__(self, width, height, title):
        self.width = width
        self.height = height
        self.parts = [
            f'<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}" viewBox="0 0 {width} {height}">',
            "<defs>",
            '<marker id="arrow" markerWidth="12" markerHeight="12" refX="10" refY="6" orient="auto" markerUnits="strokeWidth">',
            '<path d="M2,2 L10,6 L2,10 Z" fill="#334155"/>',
            "</marker>",
            '<filter id="softShadow" x="-8%" y="-8%" width="116%" height="126%">',
            '<feDropShadow dx="0" dy="3" stdDeviation="3" flood-color="#000" flood-opacity="0.12"/>',
            "</filter>",
            "</defs>",
            '<rect width="100%" height="100%" fill="#FFFFFF"/>',
            f'<text x="{width/2}" y="42" text-anchor="middle" font-family="Arial, sans-serif" font-size="24" font-weight="700" fill="{COLORS["ink"]}">{html.escape(title)}</text>',
        ]

    def text(self, x, y, text, size=13, weight=400, fill=None, anchor="middle"):
        fill = fill or COLORS["ink"]
        self.parts.append(
            f'<text x="{x:.1f}" y="{y:.1f}" text-anchor="{anchor}" font-family="Arial, sans-serif" '
            f'font-size="{size}" font-weight="{weight}" fill="{fill}">{html.escape(text)}</text>'
        )

    def wrapped_text(self, x, y, w, text, size=13, fill=None, weight=400):
        fill = fill or COLORS["ink"]
        lines = wrap(text, max(14, int(w / (size * 0.56))))
        start = y - (len(lines) - 1) * 8
        self.parts.append(
            f'<text x="{x:.1f}" y="{start:.1f}" text-anchor="middle" font-family="Arial, sans-serif" '
            f'font-size="{size}" font-weight="{weight}" fill="{fill}">'
        )
        for index, line in enumerate(lines):
            dy = 0 if index == 0 else 17
            self.parts.append(f'<tspan x="{x:.1f}" dy="{dy}">{html.escape(line)}</tspan>')
        self.parts.append("</text>")

    def block(self, x, y, w, h, title, body, color, fill="#FFFFFF"):
        self.parts.append(
            f'<rect x="{x:.1f}" y="{y:.1f}" width="{w:.1f}" height="{h:.1f}" rx="10" '
            f'fill="{fill}" stroke="{color}" stroke-width="2.2" filter="url(#softShadow)"/>'
        )
        self.parts.append(
            f'<rect x="{x:.1f}" y="{y:.1f}" width="{w:.1f}" height="38" rx="10" fill="{color}"/>'
        )
        self.text(x + w / 2, y + 25, title, size=14, weight=700, fill="#FFFFFF")
        self.wrapped_text(x + w / 2, y + 38 + (h - 38) / 2, w - 24, body, size=12, fill="#26313F")

    def layer(self, x, y, w, h, title, color):
        self.parts.append(
            f'<rect x="{x:.1f}" y="{y:.1f}" width="{w:.1f}" height="{h:.1f}" rx="14" '
            f'fill="#F8FAFC" stroke="{color}" stroke-width="2" stroke-dasharray="8 5"/>'
        )
        self.text(x + 18, y + 26, title, size=15, weight=700, fill=color, anchor="start")

    def arrow(self, x1, y1, x2, y2, label="", dashed=False):
        dash = ' stroke-dasharray="7 5"' if dashed else ""
        self.parts.append(
            f'<line x1="{x1:.1f}" y1="{y1:.1f}" x2="{x2:.1f}" y2="{y2:.1f}" '
            f'stroke="{COLORS["line"]}" stroke-width="2" marker-end="url(#arrow)"{dash}/>'
        )
        if label:
            self.text((x1 + x2) / 2, (y1 + y2) / 2 - 8, label, size=11, fill=COLORS["muted"])

    def poly(self, points, label="", dashed=False):
        dash = ' stroke-dasharray="7 5"' if dashed else ""
        path = " ".join(f"{x:.1f},{y:.1f}" for x, y in points)
        self.parts.append(
            f'<polyline points="{path}" fill="none" stroke="{COLORS["line"]}" stroke-width="2" marker-end="url(#arrow)"{dash}/>'
        )
        if label:
            x, y = points[len(points) // 2]
            self.text(x, y - 8, label, size=11, fill=COLORS["muted"])

    def caption(self, x, y, w, text):
        self.parts.append(
            f'<rect x="{x:.1f}" y="{y:.1f}" width="{w:.1f}" height="58" rx="10" fill="#F1F5F9" stroke="#CBD5E1"/>'
        )
        self.wrapped_text(x + w / 2, y + 31, w - 28, text, size=12, fill="#334155")

    def save(self, path):
        self.parts.append("</svg>")
        path.write_text("\n".join(self.parts), encoding="utf-8")


def overview():
    svg = Svg(1400, 820, "Proposed Environment-Mediated GOAP Architecture")

    svg.layer(55, 85, 1290, 165, "Agent planning layer", COLORS["agent"])
    svg.layer(55, 290, 1290, 185, "Environment mediation layer", COLORS["environment"])
    svg.layer(55, 515, 1290, 165, "Transport resource layer", COLORS["resource"])

    svg.block(105, 125, 245, 90, "Patient Agents", "local beliefs\ncurrent goal\nreserved resources", COLORS["agent"], "#F3F8FF")
    svg.block(450, 125, 245, 90, "GOAP Planner", "builds an action sequence from beliefs and world state", COLORS["planner"], "#F3F8FF")
    svg.block(795, 125, 245, 90, "Action Set", "movement, reservation, treatment and exit actions", COLORS["planner"], "#F3F8FF")
    svg.block(1090, 125, 205, 90, "Emergency Rollback", "invalidates plan and releases temporary modifiers", COLORS["environment"], "#FFF5F5")

    svg.block(105, 335, 245, 100, "World State Manager", "global state counter\npatient queue\nresource counts", COLORS["world"], "#F3FBF5")
    svg.block(450, 335, 245, 100, "Target Availability", "corridor, wing and gate accessibility", COLORS["environment"], "#FFF5F5")
    svg.block(795, 335, 245, 100, "Flow Controller", "priority-aware gate policy and wing occupancy control", COLORS["environment"], "#FFF5F5")
    svg.block(1090, 335, 205, 100, "Metrics Logger", "events, time series and protocol outcomes", COLORS["metric"], "#FBF7FF")

    svg.block(145, 560, 265, 85, "Navigation Targets", "entrance, waiting room, wing and corridors", COLORS["resource"], "#FFF7ED")
    svg.block(555, 560, 265, 85, "Shared Resources", "left/right cubicles\nreservation pools", COLORS["resource"], "#FFF7ED")
    svg.block(965, 560, 265, 85, "Physical Transport", "NavMesh movement through constrained space", COLORS["resource"], "#FFF7ED")

    svg.arrow(350, 170, 450, 170, "planning request")
    svg.arrow(695, 170, 795, 170, "selected actions")
    svg.arrow(1040, 170, 1090, 170, "failure")
    svg.poly([(1192, 215), (1192, 260), (228, 260), (228, 335)], "rollback")

    svg.arrow(228, 335, 228, 215, "world snapshot")
    svg.arrow(572, 335, 572, 215, "availability")
    svg.arrow(918, 335, 918, 215, "gate policy")
    svg.arrow(1192, 435, 1192, 560, "logs", dashed=True)
    svg.arrow(228, 435, 278, 560, "state")
    svg.arrow(572, 435, 688, 560, "active targets")
    svg.arrow(918, 435, 1098, 560, "routing constraints")

    svg.poly([(278, 560), (278, 505), (228, 505), (228, 435)], "updates", dashed=True)
    svg.poly([(688, 560), (688, 505), (228, 505), (228, 435)], "resource counts", dashed=True)
    svg.poly([(1098, 560), (1098, 505), (572, 505), (572, 435)], "blocked/open paths", dashed=True)

    svg.caption(
        105,
        720,
        1190,
        "The planner produces local action sequences, but the environment controls which transport nodes and resources are currently accessible. When those conditions change, agents roll back temporary reservations and replan against the updated world state.",
    )
    return svg


def compact():
    svg = Svg(1200, 620, "High-Level Component View")

    svg.block(85, 145, 230, 105, "Patient Agent", "beliefs\ngoals\ncurrent plan", COLORS["agent"], "#F3F8FF")
    svg.block(405, 145, 230, 105, "GOAP Planner", "generates transport plan", COLORS["planner"], "#F3F8FF")
    svg.block(725, 145, 230, 105, "Action Executor", "reserves resources\nmoves through nodes", COLORS["resource"], "#FFF7ED")

    svg.block(85, 370, 230, 105, "Shared World State", "queue\ncubicle counts\nchange counter", COLORS["world"], "#F3FBF5")
    svg.block(405, 370, 230, 105, "Environment Mediator", "target availability\nflow-control gates", COLORS["environment"], "#FFF5F5")
    svg.block(725, 370, 230, 105, "Hospital Resources", "corridors\nwing\nleft/right cubicles", COLORS["resource"], "#FFF7ED")
    svg.block(990, 255, 145, 105, "Metrics", "events\noutcomes", COLORS["metric"], "#FBF7FF")

    svg.arrow(315, 198, 405, 198, "request")
    svg.arrow(635, 198, 725, 198, "plan")
    svg.arrow(840, 250, 840, 370, "effects")
    svg.arrow(725, 422, 635, 422, "availability")
    svg.arrow(405, 422, 315, 422, "state")
    svg.poly([(200, 370), (200, 305), (200, 250)], "belief update")
    svg.poly([(520, 370), (520, 305), (520, 250)], "world snapshot")
    svg.poly([(840, 250), (840, 305), (990, 305)], "logs", dashed=True)
    svg.poly([(520, 370), (520, 320), (990, 320)], "logs", dashed=True)

    svg.caption(
        85,
        525,
        1050,
        "This view abstracts away the experiment pipeline. The core contribution is the coupling between GOAP planning and environment-mediated control of transport availability.",
    )
    return svg


def main():
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    overview().save(OUT_DIR / "architecture_overview_simple.svg")
    compact().save(OUT_DIR / "architecture_component_view_compact.svg")
    (OUT_DIR / "README.md").write_text(
        "# Simple Architecture Figures\n\n"
        "Use `architecture_overview_simple.svg` as the main paper figure. "
        "Use `architecture_component_view_compact.svg` only if the journal needs a smaller diagram.\n",
        encoding="utf-8",
    )
    print(f"wrote={OUT_DIR}")


if __name__ == "__main__":
    main()
