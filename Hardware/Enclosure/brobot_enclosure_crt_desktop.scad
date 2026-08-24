// ============================================================================
// Brobot enclosure — "retro all-in-one desktop" variant (Apple IIc-style).
//
// Same internal guts as the other variants (1.8" 128x160 SPI TFT display +
// ESP32-C3 SuperMini glued to its back), same SCREEN OPENING size (30mm x
// 36mm, unchanged) — the monitor housing sits on a wide integrated keyboard
// deck (plus top vent slots and two side control knobs), echoing the
// classic beige all-in-one look (monitor + keyboard base in one unit, like
// an Apple IIc). The keyboard deck is MOLDED INTO the body as one piece —
// only the back lid is a separate part.
//
// Sized to match MiMo's own footprint: 40mm x 60mm for the monitor box
// itself (the keyboard deck adds a bit more on top of that — see OUTPUT below).
//
// The "keyboard" is decorative (a grid of raised key-shaped bumps) — Brobot
// doesn't have 40 buttons to wire up, this is purely for looks.
//
// PRINTING NOTE: because the deck is now part of the body, "body" prints
// lying on the deck's flat bottom (not front-face-down like a plain box) —
// see the OUTPUT section below for the reorientation.
//
// HOW TO USE:
//   1. Install OpenSCAD (free): https://openscad.org/downloads.html
//   2. Measure YOUR actual boards and adjust the block below if needed.
//   3. Set render_part to preview/export each part.
//   4. F6 (render) then File > Export > Export as STL for each part.
// ============================================================================

// ---- what to show -----------------------------------------------------
// "assembled" = body (incl. keyboard deck) + lid in place (visual check)
// "exploded"  = the two parts pulled apart along Z (visual check)
// "body"      = body + deck as one piece, oriented for printing (print this)
// "lid"       = domed back alone, oriented for printing (print this)
render_part = "exploded"; // ["assembled","exploded","body","lid"]

$fn = 64;

// ============================================================================
// MEASURE AND ADJUST — same boards as the other variants
// ============================================================================

disp_w     = 34.0;
disp_l     = 58.0;
disp_pcb_t = 1.6;

// Screen opening — DO NOT CHANGE, kept identical to the other variants
window_w = 30.0;
window_l = 36.0;
window_off_x = 0;
window_off_y = 0;

esp_w = 18.0;
esp_l = 22.5;
esp_t = 6.0;
esp_center_x_offset = 0; // shift along X to dodge the SD-card holder — MEASURE AND ADJUST
esp_usb_side = 1;        // +1 = exits top end wall, -1 = bottom

usb_cut_w = 10.0;
usb_cut_h = 5.0;
usb_cut_z_offset = 0;

// ============================================================================
// CRT STYLING
// ============================================================================

// AXIS NOTE: the display's physical long edge (58mm, disp_l) runs along Y —
// and the display is mounted with that edge HORIZONTAL, per how Brobot's
// screen is actually used. So X (the shorter, 34mm-driven axis) is the real
// VERTICAL axis here — top/bottom decorations go on X faces, not Y faces.
// Y is real-horizontal — side decorations (the knobs) go on Y, offset in Y.

// SIZE-LOCKED TO MiMo: case_w x case_l below must land on exactly 40mm x
// 60mm (MiMo's own footprint) with the screen opening held fixed — see the
// values chosen below. Walls and every decorative element were scaled down
// to fit; there's very little slack left (this was a deliberate "go to the
// limit" tradeoff), so don't grow window_w/window_l without redoing the math.

top_bezel   = 5.0;  // real top (X), above the screen — where the vents go
bottom_chin = 5.0;  // real bottom (X), below the screen — where the deck attaches
side_bezel  = 12.0; // real left/right (Y), both sides — where the knobs go

corner_r = 4.0;

// NOTE: the recessed dark screen surround from the bigger version is gone —
// at wall=1mm there was no room left for a step without leaving an
// unprintable sliver (or a hole) at the recess floor. The bezel is now flat,
// uniformly `wall` thick, same as everywhere else.

dome_inset = 6.0;
dome_h     = 8.0; // shallower — case_d + dome_h now totals 30mm, the monitor's target depth

// side control knobs (2 round dials + 1 small square accent button),
// offset in Y (real-horizontal) to sit beside the screen, stacked via X
knob_d       = 6.0;
knob_h       = 1.5;
knob_gap_y   = 6.0;  // gap from the window's edge (real-horizontal side) — pushed further right per feedback
knob1_x_off  = 10.0; // offset from screen center, stacking direction (real-vertical)
knob2_x_off  = -4.0;
sq_button_w  = 4.0;
sq_button_h  = 1.2;
sq_button_x_off = -16.0;

// top vent slots (real top, X face) — behind the USB-C cutout so they don't intersect it
// shrunk to fit the now much shallower body (30mm monitor depth)
vent_count   = 4;
vent_slot_l  = 1.2; // slot length along Z
vent_spacing = 2.0; // pitch along Z
vent_start_z = 11.0;
vent_margin_y = 10.0;

// ============================================================================
// KEYBOARD DECK — the wide base the monitor sits on, attached at the real
// bottom (X face)
// ============================================================================

base_front_reach  = 25.0; // how far the deck extends toward the viewer (in front of the screen)
base_thickness    = 6.0;

key_rows = 3;
key_cols = 6;
key_w    = 3.0;
key_h    = 1.2;
key_margin_z = 3.0;
key_margin_y = 5.0;

// ============================================================================
// CASE GEOMETRY
// ============================================================================

case_w = window_w + top_bezel + bottom_chin;  // 40 — real-vertical extent, MATCHES MiMo
case_l = window_l + 2*side_bezel;             // 60 — real-horizontal extent, MATCHES MiMo
case_d = 22.0;                                 // depth of the monitor box, front face to lid — + dome_h(8) = 30mm total

wall  = 1.0; // thin — this is the "go to the limit" tradeoff: the 58mm board needs almost the full 60mm case_l
lid_t = 1.0;
body_depth = case_d - lid_t;

spigot_len       = 3.0;
spigot_clearance = 0.3;

base_span_y      = case_l; // the deck's extent along Y (real-horizontal) — matches the monitor exactly, no overhang
base_back_reach  = body_depth; // covers the whole main box for support, stops there — doesn't chase the dome's tip

// ============================================================================
// HELPERS
// ============================================================================

module rounded_rect(w, l, r) {
    hull() {
        for (x = [-1, 1], y = [-1, 1])
            translate([x*(w/2 - r), y*(l/2 - r)])
                circle(r = r);
    }
}

module rounded_box(w, l, h, r) {
    linear_extrude(height = h)
        rounded_rect(w, l, r);
}

disp_center_x = case_w/2 - top_bezel - window_w/2; // shifted toward the real top, leaving bottom_chin below for the base
disp_center_y = 0; // centered — side_bezel is symmetric

esp_center_x = disp_center_x + esp_center_x_offset;
esp_center_y = disp_center_y + esp_usb_side * (disp_l/2 - esp_l/2);

esp_z0 = wall + disp_pcb_t;

knob_y = window_l/2 + knob_gap_y;

// ============================================================================
// BODY — bezel, screen window, side knobs, top
// vents, USB-C cutout, and the keyboard deck — all one piece. Open at the
// back, closed only by the separate lid.
// ============================================================================

module keyboard_deck() {
    // plain slab, attached at the real bottom (-X face) — round the front
    // edge yourself in a slicer/CAD tool if you want it softer than a hard corner
    translate([-case_w/2 - base_thickness, -base_span_y/2, -base_front_reach])
        cube([base_thickness, base_span_y, base_front_reach + base_back_reach]);

    avail_z = base_front_reach - 2*key_margin_z;
    avail_y = base_span_y - 2*key_margin_y;
    row_pitch = avail_z / key_rows;
    col_pitch = avail_y / key_cols;

    for (r = [0 : key_rows - 1])
        for (c = [0 : key_cols - 1])
            translate([-case_w/2 + key_h/2, -avail_y/2 + col_pitch*(c + 0.5), -base_front_reach + key_margin_z + row_pitch*(r + 0.5)])
                cube([key_h, key_w, key_w], center = true);
}

module body() {
    difference() {
        union() {
            difference() {
                rounded_box(case_w, case_l, body_depth, corner_r);
                translate([0, 0, wall])
                    rounded_box(case_w - 2*wall, case_l - 2*wall, body_depth, corner_r);
            }

            // two round knobs, stacked (offset in X), beside the screen (offset in Y)
            translate([disp_center_x + knob1_x_off, knob_y, -knob_h])
                cylinder(d = knob_d, h = knob_h);
            translate([disp_center_x + knob2_x_off, knob_y, -knob_h])
                cylinder(d = knob_d, h = knob_h);

            // small square accent button below the knobs
            translate([disp_center_x + sq_button_x_off, knob_y, -sq_button_h])
                linear_extrude(height = sq_button_h)
                    square([sq_button_w, sq_button_w], center = true);

            // keyboard deck, molded into the body
            keyboard_deck();
        }

        // screen opening — fixed size, unchanged
        translate([disp_center_x + window_off_x, disp_center_y + window_off_y, -0.5])
            linear_extrude(height = wall + 1)
                rounded_rect(window_w, window_l, 1.5);

        // USB-C cutout through the top wall
        translate([esp_center_x - usb_cut_w/2, esp_usb_side * (case_l/2 - wall/2) - (wall + 1)/2, esp_z0 + esp_t/2 - usb_cut_h/2 + usb_cut_z_offset])
            cube([usb_cut_w, wall + 1, usb_cut_h]);

        // top vent slots (real top, +X face), behind the USB-C cutout
        for (i = [0 : vent_count - 1])
            translate([(case_w/2 - wall/2) - (wall + 1)/2, -(case_l/2 - vent_margin_y), vent_start_z + i*vent_spacing])
                cube([wall + 1, case_l - 2*vent_margin_y, vent_slot_l]);
    }
}

// ============================================================================
// LID — flat press-fit plate + bulging CRT dome, same as the plain CRT variant
// ============================================================================

module dome() {
    rx = (case_w - 2*dome_inset) / 2;
    ry = (case_l - 2*dome_inset) / 2;
    intersection() {
        scale([rx, ry, dome_h]) sphere(r = 1);
        translate([-case_w, -case_l, 0])
            cube([2*case_w, 2*case_l, dome_h + 1]);
    }
}

module lid() {
    rounded_box(case_w, case_l, lid_t, corner_r);

    translate([0, 0, -spigot_len])
        linear_extrude(height = spigot_len)
            difference() {
                rounded_rect(case_w - 2*wall - spigot_clearance, case_l - 2*wall - spigot_clearance, corner_r);
                rounded_rect(case_w - 4*wall - spigot_clearance, case_l - 4*wall - spigot_clearance, corner_r);
            }

    translate([0, 0, lid_t])
        dome();
}

// ============================================================================
// OUTPUT
// ============================================================================

if (render_part == "assembled") {
    color("Wheat") body();
    color("BurlyWood") translate([0, 0, body_depth]) lid();
} else if (render_part == "exploded") {
    color("Wheat") body();
    color("BurlyWood") translate([0, 0, body_depth + 40]) lid();
} else if (render_part == "body") {
    // body() (with the keyboard deck molded in) has the deck's flat outer
    // face at x = -(case_w/2 + base_thickness) — reorient so that face sits
    // on the print bed (Z=0) and the deck-to-vents axis becomes vertical
    translate([0, 0, case_w/2 + base_thickness])
        rotate([0, -90, 0])
            body();
} else if (render_part == "lid") {
    translate([0, 0, lid_t]) rotate([180, 0, 0]) lid();
}
