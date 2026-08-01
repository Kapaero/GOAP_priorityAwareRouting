from pathlib import Path
import html
import textwrap


OUT_DIR = Path(r"C:\Users\Глеб\Documents\GOAP\analysis_outputs\goap_flowcharts")

PALETTE = {
    "agent": "#4E79A7",
    "world": "#59A14F",
    "control": "#E15759",
    "resource": "#F28E2B",
    "data": "#B07AA1",
    "neutral": "#6B7280",
    "light": "#F7F9FC",
    "stroke": "#2F3742",
}


class Svg:
    def __init__(self, width, height, title):
        self.width = width
        self.height = height
        self.title = title
        self.parts = [
            f'<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}" viewBox="0 0 {width} {height}">',
            "<defs>",
            '<marker id="arrow" markerWidth="12" markerHeight="12" refX="10" refY="6" orient="auto" markerUnits="strokeWidth">',
            '<path d="M2,2 L10,6 L2,10 Z" fill="#2F3742"/>',
            "</marker>",
            '<filter id="shadow" x="-10%" y="-10%" width="120%" height="130%">',
            '<feDropShadow dx="0" dy="3" stdDeviation="3" flood-color="#000000" flood-opacity="0.16"/>',
            "</filter>",
            "</defs>",
            '<rect width="100%" height="100%" fill="#FFFFFF"/>',
            f'<text x="{width / 2}" y="38" text-anchor="middle" font-family="Arial, sans-serif" font-size="24" font-weight="700" fill="#17202A">{html.escape(title)}</text>',
        ]

    def text(self, x, y, value, size=13, fill="#17202A", anchor="middle", weight="400"):
        self.parts.append(
            f'<text x="{x:.1f}" y="{y:.1f}" text-anchor="{anchor}" font-family="Arial, sans-serif" '
            f'font-size="{size}" font-weight="{weight}" fill="{fill}">{html.escape(value)}</text>'
        )

    def wrapped_text(self, x, y, width, value, size=13, fill="#17202A", weight="400", line_height=17):
        lines = []
        for paragraph in value.split("\n"):
            lines.extend(textwrap.wrap(paragraph, width=max(14, int(width / (size * 0.56)))))
        start_y = y - (len(lines) - 1) * line_height / 2
        self.parts.append(
            f'<text x="{x:.1f}" y="{start_y:.1f}" text-anchor="middle" font-family="Arial, sans-serif" '
            f'font-size="{size}" font-weight="{weight}" fill="{fill}">'
        )
        for index, line in enumerate(lines):
            dy = 0 if index == 0 else line_height
            self.parts.append(f'<tspan x="{x:.1f}" dy="{dy}">{html.escape(line)}</tspan>')
        self.parts.append("</text>")

    def box(self, x, y, w, h, title, body="", color=PALETTE["neutral"], fill="#FFFFFF"):
        self.parts.append(
            f'<rect x="{x:.1f}" y="{y:.1f}" width="{w:.1f}" height="{h:.1f}" rx="8" '
            f'fill="{fill}" stroke="{color}" stroke-width="2.2" filter="url(#shadow)"/>'
        )
        self.parts.append(
            f'<rect x="{x:.1f}" y="{y:.1f}" width="{w:.1f}" height="34" rx="8" fill="{color}" stroke="{color}" stroke-width="2.2"/>'
        )
        self.parts.append(
            f'<text x="{x + w / 2:.1f}" y="{y + 22:.1f}" text-anchor="middle" font-family="Arial, sans-serif" '
            f'font-size="14" font-weight="700" fill="#FFFFFF">{html.escape(title)}</text>'
        )
        if body:
            self.wrapped_text(x + w / 2, y + 34 + (h - 34) / 2, w - 22, body, size=12, fill="#26313F")

    def note(self, x, y, w, h, body, color="#EDF2F7"):
        self.parts.append(
            f'<rect x="{x:.1f}" y="{y:.1f}" width="{w:.1f}" height="{h:.1f}" rx="8" '
            f'fill="{color}" stroke="#CBD5E1" stroke-width="1.5"/>'
        )
        self.wrapped_text(x + w / 2, y + h / 2, w - 20, body, size=12, fill="#334155")

    def arrow(self, x1, y1, x2, y2, label=None, dashed=False):
        dash = ' stroke-dasharray="7 5"' if dashed else ""
        self.parts.append(
            f'<line x1="{x1:.1f}" y1="{y1:.1f}" x2="{x2:.1f}" y2="{y2:.1f}" '
            f'stroke="{PALETTE["stroke"]}" stroke-width="2" marker-end="url(#arrow)"{dash}/>'
        )
        if label:
            self.text((x1 + x2) / 2, (y1 + y2) / 2 - 8, label, size=11, fill="#475569")

    def poly_arrow(self, points, label=None, dashed=False):
        dash = ' stroke-dasharray="7 5"' if dashed else ""
        path = " ".join([f"{x:.1f},{y:.1f}" for x, y in points])
        self.parts.append(
            f'<polyline points="{path}" fill="none" stroke="{PALETTE["stroke"]}" stroke-width="2" '
            f'marker-end="url(#arrow)"{dash}/>'
        )
        if label and len(points) >= 2:
            mid = points[len(points) // 2]
            self.text(mid[0], mid[1] - 8, label, size=11, fill="#475569")

    def save(self, path):
        self.parts.append("</svg>")
        path.write_text("\n".join(self.parts), encoding="utf-8")


def diagram_architecture():
    svg = Svg(1500, 900, "Environment-Mediated GOAP Transport Architecture")
    svg.box(60, 110, 250, 110, "MIMIC-derived Spawner", "Arrival time\ncritical flag\n2-5 min service time", PALETTE["data"], "#FBF7FF")
    svg.box(380, 110, 250, 110, "Patient Agent", "Goals\nbeliefs\ninventory\ncurrent plan", PALETTE["agent"], "#F3F8FF")
    svg.box(700, 110, 250, 110, "GOAP Planner", "Selects an action chain from beliefs and current world states", PALETTE["agent"], "#F3F8FF")
    svg.box(1020, 110, 250, 110, "Action Queue", "Ordered route/actions\nwith preconditions and effects", PALETTE["agent"], "#F3F8FF")

    svg.box(1020, 330, 250, 125, "Action Execution", "PrePerform\nNavMesh movement\nPostPerform\nEmergencyPerform", PALETTE["resource"], "#FFF7ED")
    svg.box(700, 330, 250, 125, "GWorld", "World states\npatient queue\ncubicle pools\nchange counter", PALETTE["world"], "#F3FBF5")
    svg.box(380, 330, 250, 125, "Target Availability", "Entrance/wing/corridor nodes\nactive/inactive gates\ninvert trigger", PALETTE["control"], "#FFF5F5")
    svg.box(60, 330, 250, 125, "Hospital Flow Controller", "Critical-aware gate control\nwing occupancy\nrelease/admission mode", PALETTE["control"], "#FFF5F5")

    svg.box(700, 590, 250, 110, "Diagnostics and Metrics", "Events\ntime series\nrun summary\nprotocol metrics", PALETTE["data"], "#FBF7FF")

    svg.arrow(310, 165, 380, 165, "spawn")
    svg.arrow(630, 165, 700, 165, "plan request")
    svg.arrow(950, 165, 1020, 165, "plan")
    svg.arrow(1145, 220, 1145, 330, "execute")
    svg.arrow(1020, 392, 950, 392, "state changes")
    svg.arrow(825, 330, 825, 220, "world snapshot")
    svg.arrow(700, 392, 630, 392, "availability")
    svg.arrow(380, 392, 310, 392, "gate policy")
    svg.poly_arrow([(185, 455), (185, 645), (700, 645)], "logs", dashed=True)
    svg.poly_arrow([(1145, 455), (1145, 645), (950, 645)], "logs", dashed=True)
    svg.poly_arrow([(825, 455), (825, 590)], "counters", dashed=True)

    svg.note(60, 760, 1210, 72, "Key idea: the planner is not the only controller. The environment itself changes through target availability and world-state counters, forcing agents to invalidate obsolete plans and replan online.")
    return svg


def diagram_agent_loop():
    svg = Svg(1500, 920, "Proposed Agent Loop: Execute, Detect, Roll Back, Replan")
    svg.box(80, 110, 220, 90, "LateUpdate", "Agent tick", PALETTE["agent"], "#F3F8FF")
    svg.box(370, 110, 240, 90, "Plan Valid?", "current action exists\ntarget is active", PALETTE["agent"], "#F3F8FF")
    svg.box(680, 110, 240, 90, "Build Plan", "GOAP planner reads beliefs + GWorld", PALETTE["agent"], "#F3F8FF")
    svg.box(990, 110, 240, 90, "Action Queue", "next action selected", PALETTE["agent"], "#F3F8FF")

    svg.box(990, 285, 240, 105, "PrePerform", "reserve cubicle\nset target\napply modifiers", PALETTE["resource"], "#FFF7ED")
    svg.box(680, 285, 240, 105, "Move to Target", "NavMesh destination\nspread threshold\narrival check", PALETTE["resource"], "#FFF7ED")
    svg.box(370, 285, 240, 105, "Target Available?", "late check catches inactive or missing node", PALETTE["control"], "#FFF5F5")
    svg.box(80, 285, 220, 105, "EmergencyPerform", "rollback reservation\nclear target\ninvalidate plan", PALETTE["control"], "#FFF5F5")

    svg.box(80, 515, 220, 100, "Wait 1 Frame", "avoid rebuilding against the same stale world", PALETTE["neutral"], "#F8FAFC")
    svg.box(370, 515, 240, 100, "World Changed?", "global world-state change counter", PALETTE["world"], "#F3FBF5")
    svg.box(680, 515, 240, 100, "PostPerform", "belief effects\nworld effects\nmetrics", PALETTE["world"], "#F3FBF5")
    svg.box(990, 515, 240, 100, "Goal Complete?", "continue action queue\nor finish goal/home", PALETTE["agent"], "#F3F8FF")

    svg.arrow(300, 155, 370, 155)
    svg.arrow(610, 155, 680, 155, "no")
    svg.arrow(920, 155, 990, 155)
    svg.arrow(1110, 200, 1110, 285)
    svg.arrow(990, 338, 920, 338)
    svg.arrow(680, 338, 610, 338)
    svg.arrow(370, 338, 300, 338, "no")
    svg.arrow(190, 390, 190, 515)
    svg.arrow(300, 565, 370, 565)
    svg.poly_arrow([(610, 565), (650, 565), (650, 155), (680, 155)], "yes")
    svg.poly_arrow([(370, 565), (330, 565), (330, 155), (370, 155)], "no", dashed=True)
    svg.poly_arrow([(370, 285), (370, 235), (1110, 235), (1110, 285)], "yes")
    svg.arrow(800, 390, 800, 515, "arrived + duration")
    svg.arrow(920, 565, 990, 565)
    svg.poly_arrow([(1110, 615), (1110, 760), (80, 760), (80, 155)], "next tick")

    svg.note(80, 800, 1150, 70, "EmergencyPerform is the safety valve: it removes temporary action modifiers, returns reserved resources, and lets the agent build a new plan only after the world has actually changed.")
    return svg


def diagram_transport_flow():
    svg = Svg(1500, 940, "Priority-Aware Patient Transport and Resource Flow")
    svg.box(70, 115, 205, 90, "Spawn", "scheduled arrival\npriority flag", PALETTE["data"], "#FBF7FF")
    svg.box(330, 115, 205, 90, "Entrance", "initial transport node", PALETTE["resource"], "#FFF7ED")
    svg.box(590, 115, 245, 90, "Waiting Room Queue", "stable order\ncritical patients first", PALETTE["world"], "#F3FBF5")
    svg.box(910, 115, 245, 90, "Cubicle Reservation", "right or left resource\nreserved before wing entry", PALETTE["resource"], "#FFF7ED")
    svg.box(1230, 115, 205, 90, "Wing Entry", "controlled gate", PALETTE["control"], "#FFF5F5")

    svg.box(280, 330, 230, 95, "Right Wing Route", "short: Corridor A\ndetour: B -> C", PALETTE["agent"], "#F3F8FF")
    svg.box(625, 330, 230, 95, "Left Wing Route", "short: Corridor B\ndetour: A -> C", PALETTE["agent"], "#F3F8FF")
    svg.box(970, 330, 250, 95, "Triage Contact", "cubicle occupied\n2-5 min service window", PALETTE["resource"], "#FFF7ED")

    svg.box(280, 565, 230, 95, "Exit Route", "exit nodes mirror entry nodes", PALETTE["agent"], "#F3F8FF")
    svg.box(625, 565, 230, 95, "Wing Exit", "register patient leaving wing", PALETTE["control"], "#FFF5F5")
    svg.box(970, 565, 250, 95, "Home / Removed", "patient complete\nmetrics recorded", PALETTE["data"], "#FBF7FF")

    svg.box(70, 565, 160, 95, "Flow Policy", "open short path for critical\nrelease when needed", PALETTE["control"], "#FFF5F5")

    svg.arrow(275, 160, 330, 160)
    svg.arrow(535, 160, 590, 160)
    svg.arrow(835, 160, 910, 160)
    svg.arrow(1155, 160, 1230, 160)
    svg.poly_arrow([(1332, 205), (1332, 270), (395, 270), (395, 330)])
    svg.poly_arrow([(1332, 205), (1332, 290), (740, 290), (740, 330)])
    svg.arrow(510, 377, 625, 377, "detour option")
    svg.arrow(855, 377, 970, 377)
    svg.arrow(1095, 425, 1095, 565)
    svg.arrow(970, 612, 855, 612)
    svg.arrow(625, 612, 510, 612)
    svg.arrow(230, 612, 280, 612, "availability")
    svg.poly_arrow([(395, 565), (395, 485), (1095, 485), (1095, 565)], "exit gates")
    svg.arrow(1220, 612, 1280, 612)
    svg.poly_arrow([(150, 565), (150, 205), (1230, 205)], "gate control", dashed=True)
    svg.poly_arrow([(150, 565), (150, 260), (590, 260)], "queue pressure", dashed=True)

    svg.note(70, 780, 1240, 72, "The transport task is separated from clinical diagnosis. Priority affects queue ordering and environmental gates; service time only represents a bounded triage-contact interval.")
    return svg


def diagram_experiment():
    svg = Svg(1500, 900, "Experimental Pipeline and Reported Outcomes")
    svg.box(70, 120, 250, 110, "MIMIC-derived Arrivals", "300 scheduled patients\n67 critical\nempirical time-of-day pattern", PALETTE["data"], "#FBF7FF")
    svg.box(390, 120, 250, 110, "Protocol Service Window", "triage contact\n2-5 minutes\nper patient", PALETTE["data"], "#FBF7FF")
    svg.box(710, 120, 250, 110, "Load Sweep", "x2.00\nx1.50\nx1.25\nx1.00", PALETTE["neutral"], "#F8FAFC")
    svg.box(1030, 120, 290, 110, "Four Controllers", "FSM / Reactive\nPriority Queue\nDecision Table\nProposed GOAP Replanner", PALETTE["agent"], "#F3F8FF")

    svg.box(235, 360, 270, 115, "Unity Simulation", "NavMesh transport\ncubicle resources\ndynamic gates", PALETTE["resource"], "#FFF7ED")
    svg.box(615, 360, 270, 115, "Event Logs", "spawn\nwaiting room\nwing entry\ntreatment complete\nhome", PALETTE["data"], "#FBF7FF")
    svg.box(995, 360, 270, 115, "Protocol Metrics", "contact <= 5 min\ntime to triage <= 15 min\ncritical tail latency", PALETTE["world"], "#F3FBF5")

    svg.box(235, 610, 270, 115, "Throughput", "run finish time\ncompleted patients\nqueue pressure", PALETTE["world"], "#F3FBF5")
    svg.box(615, 610, 270, 115, "Priority Outcome", "critical average delay\ncritical p95 delay\nbreach rate", PALETTE["control"], "#FFF5F5")
    svg.box(995, 610, 270, 115, "Figures", "boxplots\nload curves\nbreach-rate charts", PALETTE["data"], "#FBF7FF")

    svg.arrow(320, 175, 390, 175)
    svg.arrow(640, 175, 710, 175)
    svg.arrow(960, 175, 1030, 175)
    svg.poly_arrow([(1175, 230), (1175, 295), (370, 295), (370, 360)])
    svg.arrow(505, 417, 615, 417)
    svg.arrow(885, 417, 995, 417)
    svg.arrow(370, 475, 370, 610)
    svg.arrow(750, 475, 750, 610)
    svg.arrow(1130, 475, 1130, 610)
    svg.arrow(505, 667, 615, 667)
    svg.arrow(885, 667, 995, 667)

    svg.note(70, 790, 1240, 60, "No clinical decision model is claimed. MIMIC and protocol limits provide realistic temporal pressure for evaluating transport and flow-control behavior.")
    return svg


def main():
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    diagrams = {
        "figure_1_architecture.svg": diagram_architecture(),
        "figure_2_agent_replanning_loop.svg": diagram_agent_loop(),
        "figure_3_transport_resource_flow.svg": diagram_transport_flow(),
        "figure_4_experiment_pipeline.svg": diagram_experiment(),
    }
    for name, svg in diagrams.items():
        svg.save(OUT_DIR / name)

    captions = """# GOAP Flowchart Figures

These figures describe the proposed transport-oriented GOAP system without mathematical notation.

1. `figure_1_architecture.svg` - Environment-mediated GOAP transport architecture.
2. `figure_2_agent_replanning_loop.svg` - Online execution, emergency rollback, and replanning loop.
3. `figure_3_transport_resource_flow.svg` - Priority-aware patient transport, queueing, cubicle reservation, and exit flow.
4. `figure_4_experiment_pipeline.svg` - Experimental setup from MIMIC-derived arrivals to protocol metrics and plots.

Suggested paper wording:

Figure 1 shows the system-level architecture. Agents plan over local beliefs and global world states, while the environment actively changes target availability and resource accessibility. Figure 2 details the online replanning loop used when an action target becomes unavailable. Figure 3 illustrates the transport and resource-flow model used in the hospital scenario. Figure 4 summarizes the experimental pipeline and outcome metrics.
"""
    (OUT_DIR / "README.md").write_text(captions, encoding="utf-8")
    print(f"wrote={OUT_DIR}")


if __name__ == "__main__":
    main()
