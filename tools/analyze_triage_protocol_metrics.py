import csv
import math
import re
from collections import defaultdict
from pathlib import Path


EXPERIMENT_ROOT = Path(r"C:\UnityProgects\GOAP_5Attempt\GOAP_Diagnostics\Experiments")
OUTPUT_ROOT = Path(r"C:\Users\Глеб\Documents\GOAP\analysis_outputs")
EXPERIMENT_PATTERN = "triage_mimic_heavy_day_service_2_5min_load_sweep_until_clear_*"

TRIAGE_CONTACT_LIMIT_SECONDS = 300.0
TIME_TO_TRIAGE_LIMIT_SECONDS = 900.0

ARCH_ORDER = [
    "FsmReactiveController",
    "PriorityQueueDispatcher",
    "DecisionTableController",
    "ProposedEnvironmentMediatedReplanner",
]
ARCH_LABEL = {
    "FsmReactiveController": "FSM",
    "PriorityQueueDispatcher": "Priority",
    "DecisionTableController": "Decision",
    "ProposedEnvironmentMediatedReplanner": "Proposed",
}
ARCH_COLOR = {
    "FsmReactiveController": "#4E79A7",
    "PriorityQueueDispatcher": "#F28E2B",
    "DecisionTableController": "#59A14F",
    "ProposedEnvironmentMediatedReplanner": "#E15759",
}


def latest_experiment_dir():
    candidates = sorted(
        [path for path in EXPERIMENT_ROOT.glob(EXPERIMENT_PATTERN) if path.is_dir()],
        key=lambda path: path.stat().st_mtime,
        reverse=True,
    )
    if not candidates:
        raise FileNotFoundError(f"No experiment directory matches {EXPERIMENT_PATTERN}")
    return candidates[0]


def parse_float(value, default=None):
    if value is None or value == "":
        return default
    return float(str(value).replace(",", "."))


def parse_bool(value):
    return str(value).strip().lower() == "true"


SERVICE_RE = re.compile(r"(?:^|\s)service_seconds=([0-9]+(?:\.[0-9]+)?)")


def parse_service_seconds(detail):
    if not detail:
        return None
    match = SERVICE_RE.search(detail)
    if not match:
        return None
    return float(match.group(1))


def percentile(values, p):
    clean = sorted(value for value in values if value is not None and not math.isnan(value))
    if not clean:
        return None
    index = math.ceil(len(clean) * p) - 1
    index = max(0, min(len(clean) - 1, index))
    return clean[index]


def average(values):
    clean = [value for value in values if value is not None and not math.isnan(value)]
    if not clean:
        return None
    return sum(clean) / len(clean)


def five_number(values):
    clean = sorted(value for value in values if value is not None and not math.isnan(value))
    if not clean:
        return None
    return {
        "min": clean[0],
        "q1": percentile(clean, 0.25),
        "median": percentile(clean, 0.50),
        "q3": percentile(clean, 0.75),
        "max": clean[-1],
    }


def load_events(path):
    with path.open(newline="", encoding="utf-8-sig") as handle:
        return list(csv.DictReader(handle))


def load_summary(path):
    with path.open(newline="", encoding="utf-8-sig") as handle:
        return list(csv.DictReader(handle))


def build_patient_records(events):
    grouped = defaultdict(list)
    for row in events:
        agent = row.get("agent", "")
        if not agent:
            continue
        grouped[(row["run_index"], agent)].append(row)

    records = []
    for (run_index, agent), rows in grouped.items():
        spawn = next((row for row in rows if row["event"] == "spawn"), None)
        treatment = next((row for row in rows if row["event"] == "treatment_complete"), None)
        home_events = [row for row in rows if row["event"] == "home"]
        waiting = next((row for row in rows if row["event"] == "waiting_room_entry"), None)
        wing = next((row for row in rows if row["event"] == "wing_entry"), None)

        if spawn is None or treatment is None or not home_events:
            continue

        home = home_events[-1]
        service_seconds = parse_service_seconds(spawn.get("detail", ""))
        spawn_time = parse_float(spawn["simulation_elapsed_seconds"])
        treatment_complete_time = parse_float(treatment["simulation_elapsed_seconds"])
        home_time = parse_float(home["simulation_elapsed_seconds"])

        triage_start_estimate = None
        time_to_triage = None
        if service_seconds is not None:
            triage_start_estimate = treatment_complete_time - service_seconds
            time_to_triage = triage_start_estimate - spawn_time

        records.append(
            {
                "run_index": int(run_index),
                "architecture": spawn["architecture"],
                "load_multiplier": parse_float(spawn["load_multiplier"]),
                "agent": agent,
                "is_critical": parse_bool(spawn["is_critical"]),
                "spawn_time": spawn_time,
                "waiting_entry_time": parse_float(waiting["simulation_elapsed_seconds"]) if waiting else None,
                "wing_entry_time": parse_float(wing["simulation_elapsed_seconds"]) if wing else None,
                "triage_start_estimate": triage_start_estimate,
                "treatment_complete_time": treatment_complete_time,
                "home_time": home_time,
                "service_seconds": service_seconds,
                "time_to_triage_seconds": time_to_triage,
                "total_seconds": home_time - spawn_time,
            }
        )

    return records


def write_csv(path, rows, fieldnames):
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(handle, fieldnames=fieldnames)
        writer.writeheader()
        writer.writerows(rows)


def summarize_protocol(records):
    rows = []
    for key, group in sorted(
        defaultdict(list, {
            key: [record for record in records if key == (record["load_multiplier"], record["architecture"])]
            for key in sorted({(record["load_multiplier"], record["architecture"]) for record in records})
        }).items()
    ):
        load, architecture = key
        total = len(group)
        critical = [record for record in group if record["is_critical"]]
        normal = [record for record in group if not record["is_critical"]]

        def count_breaches(items, field, limit):
            return sum(1 for item in items if item[field] is not None and item[field] > limit)

        def value_list(items, field):
            return [item[field] for item in items if item[field] is not None]

        rows.append(
            {
                "load_multiplier": f"{load:.2f}",
                "architecture": architecture,
                "patients": total,
                "critical_patients": len(critical),
                "contact_over_5min": count_breaches(group, "service_seconds", TRIAGE_CONTACT_LIMIT_SECONDS),
                "contact_over_5min_pct": f"{100.0 * count_breaches(group, 'service_seconds', TRIAGE_CONTACT_LIMIT_SECONDS) / total:.2f}",
                "time_to_triage_over_15min": count_breaches(group, "time_to_triage_seconds", TIME_TO_TRIAGE_LIMIT_SECONDS),
                "time_to_triage_over_15min_pct": f"{100.0 * count_breaches(group, 'time_to_triage_seconds', TIME_TO_TRIAGE_LIMIT_SECONDS) / total:.2f}",
                "critical_time_to_triage_over_15min": count_breaches(critical, "time_to_triage_seconds", TIME_TO_TRIAGE_LIMIT_SECONDS),
                "critical_time_to_triage_over_15min_pct": f"{100.0 * count_breaches(critical, 'time_to_triage_seconds', TIME_TO_TRIAGE_LIMIT_SECONDS) / len(critical):.2f}",
                "normal_time_to_triage_over_15min": count_breaches(normal, "time_to_triage_seconds", TIME_TO_TRIAGE_LIMIT_SECONDS),
                "normal_time_to_triage_over_15min_pct": f"{100.0 * count_breaches(normal, 'time_to_triage_seconds', TIME_TO_TRIAGE_LIMIT_SECONDS) / len(normal):.2f}",
                "avg_time_to_triage_min": f"{average(value_list(group, 'time_to_triage_seconds')) / 60.0:.2f}",
                "p95_time_to_triage_min": f"{percentile(value_list(group, 'time_to_triage_seconds'), 0.95) / 60.0:.2f}",
                "avg_critical_time_to_triage_min": f"{average(value_list(critical, 'time_to_triage_seconds')) / 60.0:.2f}",
                "p95_critical_time_to_triage_min": f"{percentile(value_list(critical, 'time_to_triage_seconds'), 0.95) / 60.0:.2f}",
                "avg_total_min": f"{average(value_list(group, 'total_seconds')) / 60.0:.2f}",
                "p95_total_min": f"{percentile(value_list(group, 'total_seconds'), 0.95) / 60.0:.2f}",
            }
        )
    return rows


def svg_text(x, y, text, size=12, fill="#222", anchor="start", weight="normal"):
    return (
        f'<text x="{x:.2f}" y="{y:.2f}" font-family="Arial, sans-serif" '
        f'font-size="{size}" fill="{fill}" text-anchor="{anchor}" font-weight="{weight}">{text}</text>'
    )


def svg_line(x1, y1, x2, y2, stroke="#999", width=1, dash=None):
    dash_attr = f' stroke-dasharray="{dash}"' if dash else ""
    return f'<line x1="{x1:.2f}" y1="{y1:.2f}" x2="{x2:.2f}" y2="{y2:.2f}" stroke="{stroke}" stroke-width="{width}"{dash_attr}/>'


def svg_rect(x, y, w, h, fill="none", stroke="#333", width=1, opacity=1.0):
    return (
        f'<rect x="{x:.2f}" y="{y:.2f}" width="{w:.2f}" height="{h:.2f}" '
        f'fill="{fill}" stroke="{stroke}" stroke-width="{width}" opacity="{opacity}"/>'
    )


def scale_y(value, min_value, max_value, top, bottom):
    if max_value <= min_value:
        return bottom
    return bottom - (value - min_value) / (max_value - min_value) * (bottom - top)


def make_boxplot_svg(path, records, field, title, y_label, threshold=None, critical_only=False):
    loads = sorted({record["load_multiplier"] for record in records}, reverse=True)
    filtered = [record for record in records if (record["is_critical"] or not critical_only)]
    values = [
        record[field] / 60.0
        for record in filtered
        if record[field] is not None and not math.isnan(record[field])
    ]
    if threshold is not None:
        values.append(threshold / 60.0)
    max_value = max(values) * 1.08
    min_value = 0.0

    width = 1280
    height = 760
    margin_left = 72
    margin_right = 28
    margin_top = 70
    margin_bottom = 90
    panel_gap = 28
    plot_width = width - margin_left - margin_right
    panel_width = (plot_width - panel_gap * (len(loads) - 1)) / len(loads)
    plot_top = margin_top
    plot_bottom = height - margin_bottom
    box_width = 38

    parts = [
        f'<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}" viewBox="0 0 {width} {height}">',
        '<rect width="100%" height="100%" fill="#ffffff"/>',
        svg_text(width / 2, 32, title, size=21, anchor="middle", weight="bold"),
        svg_text(24, (plot_top + plot_bottom) / 2, y_label, size=13, anchor="middle"),
    ]

    for tick in range(0, int(math.ceil(max_value)) + 1, max(1, int(math.ceil(max_value / 6)))):
        y = scale_y(tick, min_value, max_value, plot_top, plot_bottom)
        parts.append(svg_line(margin_left - 6, y, width - margin_right, y, stroke="#E6E6E6"))
        parts.append(svg_text(margin_left - 10, y + 4, str(tick), size=11, fill="#555", anchor="end"))

    if threshold is not None:
        threshold_minutes = threshold / 60.0
        y = scale_y(threshold_minutes, min_value, max_value, plot_top, plot_bottom)
        parts.append(svg_line(margin_left, y, width - margin_right, y, stroke="#C44", width=2, dash="6 5"))
        parts.append(svg_text(width - margin_right - 4, y - 6, f"{threshold_minutes:.0f} min limit", size=12, fill="#A33", anchor="end", weight="bold"))

    for load_index, load in enumerate(loads):
        panel_x = margin_left + load_index * (panel_width + panel_gap)
        parts.append(svg_line(panel_x, plot_top, panel_x, plot_bottom, stroke="#CCCCCC"))
        parts.append(svg_line(panel_x + panel_width, plot_top, panel_x + panel_width, plot_bottom, stroke="#CCCCCC"))
        parts.append(svg_text(panel_x + panel_width / 2, plot_top - 16, f"Load x{load:.2f}", size=14, anchor="middle", weight="bold"))

        arch_gap = panel_width / (len(ARCH_ORDER) + 1)
        for arch_index, architecture in enumerate(ARCH_ORDER):
            group = [
                record[field] / 60.0
                for record in filtered
                if record["load_multiplier"] == load
                and record["architecture"] == architecture
                and record[field] is not None
            ]
            stats = five_number(group)
            if stats is None:
                continue
            x = panel_x + arch_gap * (arch_index + 1)
            color = ARCH_COLOR[architecture]
            y_min = scale_y(stats["min"], min_value, max_value, plot_top, plot_bottom)
            y_q1 = scale_y(stats["q1"], min_value, max_value, plot_top, plot_bottom)
            y_med = scale_y(stats["median"], min_value, max_value, plot_top, plot_bottom)
            y_q3 = scale_y(stats["q3"], min_value, max_value, plot_top, plot_bottom)
            y_max = scale_y(stats["max"], min_value, max_value, plot_top, plot_bottom)
            parts.append(svg_line(x, y_min, x, y_max, stroke=color, width=2))
            parts.append(svg_line(x - box_width * 0.3, y_min, x + box_width * 0.3, y_min, stroke=color, width=2))
            parts.append(svg_line(x - box_width * 0.3, y_max, x + box_width * 0.3, y_max, stroke=color, width=2))
            parts.append(svg_rect(x - box_width / 2, y_q3, box_width, max(2, y_q1 - y_q3), fill=color, stroke=color, opacity=0.24))
            parts.append(svg_line(x - box_width / 2, y_med, x + box_width / 2, y_med, stroke=color, width=3))
            parts.append(svg_text(x, plot_bottom + 18, ARCH_LABEL[architecture], size=10, fill="#333", anchor="middle"))

    legend_x = margin_left
    legend_y = height - 38
    for index, architecture in enumerate(ARCH_ORDER):
        x = legend_x + index * 170
        parts.append(svg_rect(x, legend_y - 11, 14, 14, fill=ARCH_COLOR[architecture], stroke=ARCH_COLOR[architecture], opacity=0.5))
        parts.append(svg_text(x + 20, legend_y, ARCH_LABEL[architecture], size=12))

    parts.append("</svg>")
    path.write_text("\n".join(parts), encoding="utf-8")


def make_breach_svg(path, summary_rows):
    loads = sorted({parse_float(row["load_multiplier"]) for row in summary_rows}, reverse=True)
    width = 1180
    height = 620
    left = 72
    right = 28
    top = 70
    bottom = 95
    plot_width = width - left - right
    plot_height = height - top - bottom
    max_pct = max(parse_float(row["critical_time_to_triage_over_15min_pct"]) for row in summary_rows)
    max_pct = max(5.0, math.ceil(max_pct / 10.0) * 10.0)

    parts = [
        f'<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}" viewBox="0 0 {width} {height}">',
        '<rect width="100%" height="100%" fill="#ffffff"/>',
        svg_text(width / 2, 32, "Critical patients breaching 15 min to triage", size=21, anchor="middle", weight="bold"),
    ]
    for tick in range(0, int(max_pct) + 1, max(5, int(max_pct / 5))):
        y = scale_y(tick, 0, max_pct, top, height - bottom)
        parts.append(svg_line(left - 6, y, width - right, y, stroke="#E6E6E6"))
        parts.append(svg_text(left - 10, y + 4, f"{tick}%", size=11, fill="#555", anchor="end"))

    group_width = plot_width / len(loads)
    bar_width = group_width / (len(ARCH_ORDER) + 1.6)
    rows_by_key = {(parse_float(row["load_multiplier"]), row["architecture"]): row for row in summary_rows}
    for load_index, load in enumerate(loads):
        base_x = left + load_index * group_width
        parts.append(svg_text(base_x + group_width / 2, height - bottom + 38, f"x{load:.2f}", size=13, anchor="middle", weight="bold"))
        for arch_index, architecture in enumerate(ARCH_ORDER):
            row = rows_by_key[(load, architecture)]
            pct = parse_float(row["critical_time_to_triage_over_15min_pct"])
            x = base_x + bar_width * (arch_index + 0.8)
            y = scale_y(pct, 0, max_pct, top, height - bottom)
            h = height - bottom - y
            parts.append(svg_rect(x, y, bar_width * 0.82, h, fill=ARCH_COLOR[architecture], stroke=ARCH_COLOR[architecture], opacity=0.72))
            parts.append(svg_text(x + bar_width * 0.41, y - 5, f"{pct:.0f}", size=10, anchor="middle", fill="#333"))

    parts.append(svg_text(22, (top + height - bottom) / 2, "% critical > 15 min", size=13, anchor="middle"))
    legend_x = left
    legend_y = height - 28
    for index, architecture in enumerate(ARCH_ORDER):
        x = legend_x + index * 170
        parts.append(svg_rect(x, legend_y - 11, 14, 14, fill=ARCH_COLOR[architecture], stroke=ARCH_COLOR[architecture], opacity=0.6))
        parts.append(svg_text(x + 20, legend_y, ARCH_LABEL[architecture], size=12))
    parts.append("</svg>")
    path.write_text("\n".join(parts), encoding="utf-8")


def make_finish_svg(path, summary):
    rows = [
        {
            "load": parse_float(row["load_multiplier"]),
            "architecture": row["architecture"],
            "finish": parse_float(row["simulation_duration_seconds"]),
        }
        for row in summary
    ]
    loads = sorted({row["load"] for row in rows})
    min_finish = min(row["finish"] for row in rows) * 0.95
    max_finish = max(row["finish"] for row in rows) * 1.05
    width = 980
    height = 600
    left = 78
    right = 40
    top = 70
    bottom = 80
    parts = [
        f'<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}" viewBox="0 0 {width} {height}">',
        '<rect width="100%" height="100%" fill="#ffffff"/>',
        svg_text(width / 2, 32, "Run finish time by load", size=21, anchor="middle", weight="bold"),
    ]

    def sx(load):
        if len(loads) == 1:
            return (left + width - right) / 2
        return left + (load - min(loads)) / (max(loads) - min(loads)) * (width - left - right)

    for tick in range(int(min_finish // 250 * 250), int(max_finish) + 1, 250):
        y = scale_y(tick, min_finish, max_finish, top, height - bottom)
        parts.append(svg_line(left - 6, y, width - right, y, stroke="#E6E6E6"))
        parts.append(svg_text(left - 10, y + 4, str(tick), size=11, fill="#555", anchor="end"))

    for load in loads:
        x = sx(load)
        parts.append(svg_line(x, top, x, height - bottom, stroke="#EFEFEF"))
        parts.append(svg_text(x, height - bottom + 24, f"x{load:.2f}", size=12, anchor="middle"))

    for architecture in ARCH_ORDER:
        series = sorted([row for row in rows if row["architecture"] == architecture], key=lambda row: row["load"])
        color = ARCH_COLOR[architecture]
        points = [(sx(row["load"]), scale_y(row["finish"], min_finish, max_finish, top, height - bottom)) for row in series]
        for (x1, y1), (x2, y2) in zip(points, points[1:]):
            parts.append(svg_line(x1, y1, x2, y2, stroke=color, width=3))
        for x, y in points:
            parts.append(f'<circle cx="{x:.2f}" cy="{y:.2f}" r="5" fill="{color}" stroke="#fff" stroke-width="1.5"/>')

    parts.append(svg_text(width / 2, height - 24, "Load multiplier", size=13, anchor="middle"))
    parts.append(svg_text(22, (top + height - bottom) / 2, "finish time, sim sec", size=13, anchor="middle"))
    legend_x = left
    legend_y = height - 48
    for index, architecture in enumerate(ARCH_ORDER):
        x = legend_x + index * 170
        parts.append(f'<circle cx="{x + 7:.2f}" cy="{legend_y - 5:.2f}" r="6" fill="{ARCH_COLOR[architecture]}"/>')
        parts.append(svg_text(x + 20, legend_y, ARCH_LABEL[architecture], size=12))
    parts.append("</svg>")
    path.write_text("\n".join(parts), encoding="utf-8")


def main():
    experiment_dir = latest_experiment_dir()
    events = load_events(experiment_dir / "events.csv")
    summary = load_summary(experiment_dir / "runs_summary.csv")
    records = build_patient_records(events)
    summary_rows = summarize_protocol(records)

    output_dir = OUTPUT_ROOT / experiment_dir.name
    output_dir.mkdir(parents=True, exist_ok=True)

    write_csv(
        output_dir / "patient_protocol_records.csv",
        records,
        [
            "run_index",
            "architecture",
            "load_multiplier",
            "agent",
            "is_critical",
            "spawn_time",
            "waiting_entry_time",
            "wing_entry_time",
            "triage_start_estimate",
            "treatment_complete_time",
            "home_time",
            "service_seconds",
            "time_to_triage_seconds",
            "total_seconds",
        ],
    )
    write_csv(
        output_dir / "protocol_summary.csv",
        summary_rows,
        [
            "load_multiplier",
            "architecture",
            "patients",
            "critical_patients",
            "contact_over_5min",
            "contact_over_5min_pct",
            "time_to_triage_over_15min",
            "time_to_triage_over_15min_pct",
            "critical_time_to_triage_over_15min",
            "critical_time_to_triage_over_15min_pct",
            "normal_time_to_triage_over_15min",
            "normal_time_to_triage_over_15min_pct",
            "avg_time_to_triage_min",
            "p95_time_to_triage_min",
            "avg_critical_time_to_triage_min",
            "p95_critical_time_to_triage_min",
            "avg_total_min",
            "p95_total_min",
        ],
    )

    make_boxplot_svg(
        output_dir / "box_time_to_triage_all.svg",
        records,
        "time_to_triage_seconds",
        "Time to triage start, all patients",
        "minutes",
        threshold=TIME_TO_TRIAGE_LIMIT_SECONDS,
    )
    make_boxplot_svg(
        output_dir / "box_time_to_triage_critical.svg",
        records,
        "time_to_triage_seconds",
        "Time to triage start, critical patients",
        "minutes",
        threshold=TIME_TO_TRIAGE_LIMIT_SECONDS,
        critical_only=True,
    )
    make_boxplot_svg(
        output_dir / "box_total_time_all.svg",
        records,
        "total_seconds",
        "Total system time, all patients",
        "minutes",
    )
    make_breach_svg(output_dir / "critical_15min_breach_rate.svg", summary_rows)
    make_finish_svg(output_dir / "finish_time_by_load.svg", summary)

    print(f"experiment_dir={experiment_dir}")
    print(f"output_dir={output_dir}")
    print(f"patient_records={len(records)}")
    print(f"summary_rows={len(summary_rows)}")


if __name__ == "__main__":
    main()
