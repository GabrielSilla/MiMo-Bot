// ============================================================================
// Brobot enclosure — "retro CRT monitor" variant.
//
// Same internal guts as brobot_enclosure.scad (1.8" 128x160 SPI TFT display
// + ESP32-C3 SuperMini glued to its back), same SCREEN OPENING size (30mm x
// 36mm, unchanged as requested) — the housing around it reads as a chunky
// old tube monitor: bezel, recessed dark screen surround, rounded corners,
// a bulging dome on the back (the "tube"), and a round pedestal stand —
// MOLDED INTO the body as one piece. Only the back lid is separate.
//
// Sized to match MiMo's own footprint: 40mm x 60mm for the monitor box
// itself (the stand adds a bit more on top of that — see OUTPUT below).
//
// PRINTING NOTE: because the stand is now part of the body, "body" prints
// lying on the stand's flat face (not front-face-down like a plain box) —
// see the OUTPUT section below for the reorientation.
//
// HOW TO USE:
//   1. Install OpenSCAD (free): https://openscad.org/downloads.html
//   2. Measure YOUR actual boards and adjust the block below if needed.
//   3. Set render_part to preview/export each part.
//   4. F6 (render) then File > Export > Export as STL for each part.
// ============================================================================

// ---- what to show -----------------------------------------------------
// "assembled" = body (incl. stand) + lid in place (visual check, don't print)
// "exploded"  = the two parts pulled apart along Z (visual check)
// "body"      = body + stand as one piece, oriented for printing (print this)
// "lid"       = domed back alone, oriented for printing (print this)
render_part = "exploded"; // ["assembled","exploded","body","lid"]

$fn = 64; // smoother curves for the dome and rounded corners

// ============================================================================
// MEASURE AND ADJUST — same boards as the plain sandwich case
// ============================================================================

disp_w     = 34.0;   // display PCB width
disp_l     = 58.0;   // display PCB length
disp_pcb_t = 1.6;    // display PCB thickness

// Screen opening — DO NOT CHANGE, kept identical to the plain case per request
window_w = 30.0;
window_l = 36.0;
window_off_x = 0;
window_off_y = 0;

// ESP32-C3 SuperMini, glued to the back of the display PCB, natural
// orientation (esp_l along Y), flush against the display's top edge
esp_w = 18.0;
esp_l = 22.5;
esp_t = 6.0;
esp_center_x_offset = 0; // shift along X to dodge the SD-card holder — MEASURE AND ADJUST
esp_usb_side = 1;        // +1 = exits top end wall, -1 = bottom (header side)

usb_cut_w = 10.0;
usb_cut_h = 5.0;
usb_cut_z_offset = 0;

// ============================================================================
// CRT STYLING — this is the part that makes it look like an old monitor
// ============================================================================

// AXIS NOTE: the display's physical long edge (58mm, disp_l) runs along Y,
// mounted HORIZONTAL (that's how Brobot's screen is actually used). So X
// (the shorter, 34mm-driven axis) is the real VERTICAL axis — top/bottom
// decorations go on X faces. Y is real-horizontal — side decorations go on Y.

// SIZE-LOCKED TO MiMo: case_w x case_l below must land on exactly 40mm x
// 60mm (MiMo's own footprint) with the screen opening held fixed — see the
// values chosen below. Walls and every decorative element were scaled down
// to fit; there's very little slack left (this was a deliberate "go to the
// limit" tradeoff), so don't grow window_w/window_l without redoing the math.

top_bezel    = 4.0;  // real top (X), above the screen
side_bezel   = 12.0; // real left/right (Y), both sides of the screen
bottom_chin  = 6.0;  // real bottom (X), below the screen, for the stand

corner_r = 4.0; // soft corners, scaled down for the smaller case

// NOTE: the recessed dark screen surround from the bigger version is gone —
// at wall=1mm there was no room left for a step without leaving an
// unprintable sliver (or a hole) at the recess floor. The bezel is now flat,
// uniformly `wall` thick, same as everywhere else.
//
// NOTE: the three control buttons from the bigger version didn't fit in a
// 6mm chin and were dropped. Add small ones back (~2mm dia) if you want —
// there's a sliver of room between the recess and the case edge.

dome_inset = 6.0;  // how much smaller the back bulge is than the case footprint
dome_h     = 12.0; // how far the "tube" bulges out the back

stand_radius    = 18.0; // disc radius — a round base the case sits on top of
stand_thickness = 3.0;  // thin disc, lying flat (horizontal), not a tall blade

// ============================================================================
// CASE GEOMETRY
// ============================================================================

case_w = window_w + top_bezel + bottom_chin;    // 40 — real-vertical extent, MATCHES MiMo
case_l = window_l + 2*side_bezel;               // 60 — real-horizontal extent, MATCHES MiMo
case_d = 45.0;                                   // depth of the main body, before the dome adds more — unchanged, not part of this request

wall  = 1.0; // thin — this is the "go to the limit" tradeoff: the 58mm board needs almost the full 60mm case_l
lid_t = 1.0;
body_depth = case_d - lid_t;

spigot_len       = 3.0;
spigot_clearance = 0.3;

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

disp_center_x = case_w/2 - top_bezel - window_w/2; // shifted toward the real top, leaving bottom_chin below
disp_center_y = 0; // centered — side_bezel is symmetric

esp_center_x = disp_center_x + esp_center_x_offset;
esp_center_y = disp_center_y + esp_usb_side * (disp_l/2 - esp_l/2);

esp_z0 = wall + disp_pcb_t;

// ============================================================================
// BODY — thick bezel, recessed screen surround, screen window, USB-C
// cutout, and the pedestal stand — all one piece. Open at the back, closed
// only by the separate lid.
// ============================================================================

module stand() {
    z_center = body_depth * 0.5;
    translate([-case_w/2 - stand_thickness/2, 0, z_center])
        rotate([0, 90, 0])
            cylinder(r = stand_radius, h = stand_thickness, center = true);
}

module body() {
    difference() {
        union() {
            // outer shell, open at the back
            difference() {
                rounded_box(case_w, case_l, body_depth, corner_r);
                translate([0, 0, wall])
                    rounded_box(case_w - 2*wall, case_l - 2*wall, body_depth, corner_r);
            }

            // pedestal stand, molded into the body
            stand();
        }

        // the actual screen opening — through cut, fixed size, unchanged
        translate([disp_center_x + window_off_x, disp_center_y + window_off_y, -0.5])
            linear_extrude(height = wall + 1)
                rounded_rect(window_w, window_l, 1.5);

        // USB-C cutout through the top end wall, at the ESP32's depth
        translate([esp_center_x - usb_cut_w/2, esp_usb_side * (case_l/2 - wall/2) - (wall + 1)/2, esp_z0 + esp_t/2 - usb_cut_h/2 + usb_cut_z_offset])
            cube([usb_cut_w, wall + 1, usb_cut_h]);
    }
}

// ============================================================================
// LID — flat press-fit plate + a bulging dome on the outer back face,
// the classic CRT "tube" hump.
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
    // flat plate — the functional mating face
    rounded_box(case_w, case_l, lid_t, corner_r);

    // press-fit spigot, same mechanism as the plain case
    translate([0, 0, -spigot_len])
        linear_extrude(height = spigot_len)
            difference() {
                rounded_rect(case_w - 2*wall - spigot_clearance, case_l - 2*wall - spigot_clearance, corner_r);
                rounded_rect(case_w - 4*wall - spigot_clearance, case_l - 4*wall - spigot_clearance, corner_r);
            }

    // the tube bulge
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
    // body() (with the stand molded in) has its stand's flat outer face at
    // x = -(case_w/2 + stand_thickness) — reorient so that face sits on the
    // print bed (Z=0) and the stand-to-vents axis becomes vertical
    translate([0, 0, case_w/2 + stand_thickness])
        rotate([0, -90, 0])
            body();
} else if (render_part == "lid") {
    // flip so the flat mating face prints down, dome rising up — no supports needed
    translate([0, 0, lid_t]) rotate([180, 0, 0]) lid();
}
