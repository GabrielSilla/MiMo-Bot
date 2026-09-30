// ============================================================================
// Peemo enclosure — "retro mini PC" variant, modelled after the reference
// render: a compact off-white CRT monitor (recessed dark screen bezel, two
// round buttons + 3 LEDs on the right of the frame, softly tapered back) on a
// slim wide base with front ports (SD, USB-C, 2x USB-A) and a vented side.
//
// THREE printable parts (the bottom of the monitor is CLOSED — the display PCB
// + ESP32 go in from the back, like the older body + lid variants):
//   monitor : front shell — bezel, screen window, buttons, closed bottom/top/
//             sides. Open at the back only.
//   back    : tapered rear cover; press-fits into the monitor with a spigot ring.
//   base    : slim box with the ports, side vents and a tilted pedestal that
//             the closed monitor sits on.
//
// FIXED (must not change): screen window 30 x 36 mm, display 34 x 58 mm PCB
// pocket and its 1 mm/side clearance, ESP32 + USB-C cutout — same as
// brobot_enclosure_crt_desktop-new.scad. Everything else follows the picture.
//
// The monitor is modelled in the old "desktop" frame (X = real vertical,
// Y = real horizontal, Z = depth, front face at Z=0) and mapped to real axes
// (X right, Y away from viewer, Z up) in monitor_real().
//
// render_part: "assembled" | "exploded" | "monitor" | "back" | "base"
// PRINT: monitor front-face-down (the recessed bezel is a 0.8 mm bridge), back
// cover spigot-down, base flat on its bottom. No supports anywhere.
// ============================================================================

render_part = "assembled"; // ["assembled","exploded","monitor","back","base"]

$fn = 64;

// ---- boards / screen (FIXED) -----------------------------------------------
disp_w     = 34.0;
disp_l     = 58.0;
disp_pcb_t = 1.6;

window_w = 30.0;      // DO NOT CHANGE
window_l = 36.0;      // DO NOT CHANGE
window_off_x = 0;
window_off_y = -3.5;

esp_w = 18.0;
esp_l = 22.5;
esp_t = 6.0;
esp_center_x_offset = 0;
esp_usb_side = -1;
usb_cut_w = 10.0;
usb_cut_h = 5.0;
usb_cut_z_offset = 5.5;

// ---- monitor shape ---------------------------------------------------------
top_bezel   = 5.0;
bottom_chin = 5.0;
side_bezel  = 13.0;

case_w = window_w + top_bezel + bottom_chin;  // 40
case_l = window_l + 2*side_bezel;             // 62

corner_r       = 4.5;  // soft corners like the reference (bigger and the PCB corner touches the wall)
front_fillet   = 1.2;  // rounded front edge

wall       = 1.0;
inner_corner_r = corner_r - wall;  // concentric with the outside: PCB corner (3,4 from arc centre = 5.0) clears the 3.5 arc, wall stays 1 mm
recess     = 0.8;                 // depth of the dark screen bezel panel
front_t    = wall + recess;       // front wall thickness outside the panel (panel itself is `wall`)
body_depth = 15.5;                // straight part, front face to start of taper (USB-C slot reaches z=14.4, keep it inside)

// Tapered back — deeper and rounder than the old variants, per the reference
crt_back_h      = 22.5;
crt_rear_frac_w = 0.72;
crt_rear_frac_l = 0.66;
crt_rear_r      = 10.0;
crt_taper_pow   = 1.15;
crt_rear_drop_x = 1.5;
crt_shell_t     = 1.6;
crt_slices      = 32;

// Screen bezel panel (recessed, the "dark glass" area). Window sits inside it.
bezel_margin_x = 2.0;   // above/below the window
bezel_margin_l = 4.0;   // left of the window (real horizontal)
bezel_margin_r = 3.0;   // right of the window
bezel_r        = 3.0;

// Two round buttons stacked on the right of the frame, 3 LEDs top right
dial_d      = 6.4;
dial_depth  = 0.5;
dial_y      = window_l/2 + window_off_y + 3.0 + 5.5;   // right frame, past the bezel panel
dial_x      = [4.5, -4.5];
led_d       = 1.6;
led_x       = case_w/2 - 2.6;
led_y0      = case_l/2 - 7.0;
led_pitch   = 3.0;

// ---- base ------------------------------------------------------------------
base_w      = 63.0;
base_d      = 41.0;
base_h      = 12.0;
base_r      = 4.0;
base_front  = 6.0;    // deck sticking out in front of the monitor's front face
neck_h      = 2.0;
neck_depth = 15.0;
neck_in_h = 13.0;// pedestal height — the visible gap under the monitor
tilt        = 3.0;    // screen tilt back, degrees
spigot_len       = 3.0;   // back-cover ring that plugs into the front shell
spigot_clearance = 0.3;
port_depth  = 5.0;

// ---- alignment pins: monitor bottom -> pedestal holes ------------------------
pin_d      = 3.0;
pin_len    = 3.0;    // sticks out of the monitor's bottom face
pin_x      = 20.0;   // +/- from the centre, along the width
pin_z      = 8.5;    // depth from the front face (clear of PCB, ESP32 and the back-cover ring)
hole_clear = 0.2;    // extra diameter of the holes
hole_depth = pin_len + 1.0;
boss_d     = 6.0;    // optional inner boss under each pin. 0 = none: the pin is flush inside, so the PCB slides in freely
boss_h     = 0.0;

// ============================================================================
// helpers
// ============================================================================
module rounded_rect(w, l, r) {
    hull() for (x = [-1, 1], y = [-1, 1])
        translate([x*(w/2 - r), y*(l/2 - r)]) circle(r = r);
}
module rounded_box(w, l, h, r) linear_extrude(height = h) rounded_rect(w, l, r);

disp_center_x = case_w/2 - top_bezel - window_w/2;
esp_center_x  = disp_center_x + esp_center_x_offset;
esp_z0        = front_t + disp_pcb_t;

function crt_ease(t) = pow(t, crt_taper_pow);

module crt_slice(t, inset, h) {
    e = crt_ease(t);
    w = case_w - case_w * (1 - crt_rear_frac_w) * e - 2*inset;
    l = case_l - case_l * (1 - crt_rear_frac_l) * e - 2*inset;
    r = corner_r + (crt_rear_r - corner_r) * e;
    translate([-crt_rear_drop_x * e, 0, h * t])
        linear_extrude(height = 0.01)
            rounded_rect(w, l, min(r, min(w, l)/2 - 0.01));
}
module crt_back_solid(inset, h) {
    for (i = [0 : crt_slices - 1])
        hull() { crt_slice(i / crt_slices, inset, h); crt_slice((i + 1) / crt_slices, inset, h); }
}

// ============================================================================
// MONITOR (old frame)
// ============================================================================
module usb_cut_cube(extra = 0) {
    translate([esp_center_x - usb_cut_w/2, esp_usb_side * (case_l/2 - wall/2) - (wall + 1)/2 - extra,
               esp_z0 + esp_t/2 - usb_cut_h/2 + usb_cut_z_offset])
        cube([usb_cut_w, wall + 1 + 2*extra, usb_cut_h]);
}

// FRONT SHELL — closed everywhere except the back face (z = body_depth)
module monitor_old() {
    bezel_w = window_w + 2*bezel_margin_x;
    bezel_l = window_l + bezel_margin_l + bezel_margin_r;
    bezel_cy = window_off_y + (bezel_margin_r - bezel_margin_l)/2;

    union() {
    difference() {
        hull() {
            linear_extrude(height = 0.01)
                rounded_rect(case_w - 2*front_fillet, case_l - 2*front_fillet, corner_r - front_fillet);
            translate([0, 0, front_fillet]) rounded_box(case_w, case_l, body_depth - front_fillet, corner_r);
        }

        // cavity, open through the back face
        translate([0, 0, front_t]) rounded_box(case_w - 2*wall, case_l - 2*wall, body_depth, inner_corner_r);

        // recessed screen bezel panel
        translate([disp_center_x, bezel_cy, -0.01])
            linear_extrude(height = recess + 0.01) rounded_rect(bezel_w, bezel_l, bezel_r);

        // screen opening — FIXED size
        translate([disp_center_x + window_off_x, window_off_y, -0.5])
            linear_extrude(height = front_t + 1) rounded_rect(window_w, window_l, 1.5);

        // USB-C cutout through the end wall
        usb_cut_cube();

        // two round buttons + LEDs (engraved)
        for (x = dial_x)
            translate([x, dial_y, -0.01]) cylinder(d = dial_d, h = dial_depth);
        for (i = [0 : 2])
            translate([led_x, led_y0 - i*led_pitch, -0.01]) cylinder(d = led_d, h = dial_depth);
    }

    // two locating pins on the bottom face (-X), through the wall and flush with its inner face
    for (sy = [-1, 1])
        translate([-case_w/2, sy*pin_x, pin_z]) rotate([0, -90, 0]) {
            if (boss_h > 0) translate([0, 0, -wall - boss_h]) cylinder(d = boss_d, h = boss_h + 0.01);   // inside the cavity
            translate([0, 0, -wall - 0.01]) cylinder(d = pin_d, h = wall + pin_len - 0.5);
            translate([0, 0, pin_len - 0.51]) cylinder(d1 = pin_d, d2 = pin_d - 1.0, h = 0.5);  // chamfered tip
        }
    }
}

// BACK COVER — hollow taper on top of a press-fit spigot ring (old frame, mating face at z = body_depth)
module back_old() {
    difference() {
        union() {
            translate([0, 0, body_depth]) difference() {
                crt_back_solid(0, crt_back_h);
                // hollow, open at the mating face
                translate([0, 0, -0.5]) crt_back_solid(crt_shell_t, crt_back_h - crt_shell_t + 0.5);
            }
            translate([0, 0, body_depth - spigot_len])
                linear_extrude(height = spigot_len + 0.01)
                    difference() {
                        rounded_rect(case_w - 2*wall - spigot_clearance, case_l - 2*wall - spigot_clearance, inner_corner_r);
                        rounded_rect(case_w - 4*wall - spigot_clearance, case_l - 4*wall - spigot_clearance, max(inner_corner_r - wall, 0.3));
                    }
        }
        // keep the USB-C slot clear of the spigot ring
        usb_cut_cube(extra = 1.5);
    }
}

// real X = old Y, real Y = old Z, real Z = old X. Shell bottom (old X = -case_w/2)
// lands on Z = 0, front face on Y = 0.
module to_real() {
    translate([0, 0, case_w/2])
        multmatrix([[0,1,0,0],[0,0,1,0],[1,0,0,0],[0,0,0,1]]) children();
}

// ============================================================================
// BASE
// ============================================================================
// Tilt frame: pivot at the pedestal's rear-bottom edge, so the front lifts.
module in_tilt_frame() {
    translate([0, neck_depth, neck_in_h + neck_h]) rotate([-tilt, 0, 0]) translate([0, -neck_depth, 0]) children();
}

module base() {
    y0 = -base_front;
    difference() {
        union() {
            // slab, soft top edge
            translate([0, y0 + base_d/2, 0])
                hull() {
                    linear_extrude(height = 0.01) rounded_rect(base_w - 2, base_d - 2, base_r);
                    translate([0, 0, 1]) linear_extrude(height = base_h - 2) rounded_rect(base_w, base_d, base_r);
                    translate([0, 0, base_h - 0.01]) linear_extrude(height = 0.01) rounded_rect(base_w - 2, base_d - 2, base_r);
                }

            // tilted pedestal, slightly inset from the monitor outline, clipped above z = 0.
            // rounded_box is CENTRED: spans y = 0.5 .. body_depth + 0.5 under the closed shell
            intersection() {
                in_tilt_frame()
                    translate([0, (0.5 + body_depth + 0.5)/2, -40]) rounded_box(case_l - 2, body_depth, 40, 3);
                translate([-50, -50, 0]) cube([100, 200, 100]);
            }
        }

        // two pin holes in the pedestal top (tilt frame: monitor bottom plane is local z = 0)
        in_tilt_frame()
            for (sx = [-1, 1])
                translate([sx*pin_x, pin_z, -hole_depth]) cylinder(d = pin_d + hole_clear, h = hole_depth + 1);

        // front ports, left to right like the reference: SD slot (+ card notch), USB-C, USB-A x2.
        // Real pockets (port_depth deep) with the inner tongue left standing on the USB-A ones.
        pz = base_h/2;
        translate([-29, y0 - 0.01, pz - 0.9]) cube([14, port_depth, 1.8]);        // SD slot
        translate([-25, y0 - 0.01, pz - 3.4]) cube([6, port_depth, 1.0]);         // small slot below it
        translate([-9,  y0 - 0.01, pz - 1.4]) cube([9, port_depth, 2.8]);         // USB-C
        for (x = [4, 18])
            difference() {                                                         // USB-A
                translate([x, y0 - 0.01, pz - 2.2]) cube([11, port_depth, 4.4]);
                translate([x + 1, y0 - 0.02, pz - 0.5]) cube([9, port_depth - 0.6, 1.0]);   // tongue
            }

        // right-side vent grille: vertical slots near the back
        for (i = [0 : 8])
            translate([base_w/2 - port_depth, y0 + base_d - 7 - i*2.4, base_h/2 - 3.0])
                cube([port_depth + 0.01, 1.2, 6.0]);
    }
}

// ============================================================================
// OUTPUT
// ============================================================================
module assembled_monitor() {
    in_tilt_frame() to_real() { color("Ivory") monitor_old(); color("Wheat") back_old(); }
}

if (render_part == "assembled") {
    color("Beige") base();
    assembled_monitor();
} else if (render_part == "exploded") {
    color("Beige") base();
    translate([0, 0, 30]) in_tilt_frame() to_real() {
        color("Ivory") monitor_old();
        color("Wheat") translate([0, 0, 25]) back_old();
    }
} else if (render_part == "monitor") {
    // front face down; the open back faces up
    multmatrix([[1,0,0,0],[0,0,-1,0],[0,1,0,0],[0,0,0,1]]) to_real() monitor_old();
} else if (render_part == "back") {
    // spigot ring on the bed, taper rising from it: every layer smaller than the one below
    translate([0, 0, spigot_len - body_depth]) back_old();
} else if (render_part == "base") {
    base();
}
