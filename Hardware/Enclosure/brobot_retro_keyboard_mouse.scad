// ============================================================================
// Peemo retro mini PC — decorative KEYBOARD + MOUSE, ONE printed piece.
//
// Pure desk ornament: fits nothing, touches none of the other parts. Sized to
// go with brobot_enclosure_retro_pc.scad (monitor 62 mm wide, base 63 mm): the
// keyboard is about as wide as the monitor/base (~63 mm), with a reduced key
// count (4 rows, 3-column numpad) and a low, flat mouse.
//
// The two are joined by a thin mouse-pad plate so they print as a single
// piece. Prints flat on the plate, no supports.
//
// render_part: "piece" (print this)
// ============================================================================

$fn = 48;

// ---- plate ------------------------------------------------------------------
plate_t   = 1.2;
plate_r   = 4.0;
plate_gap = 7.0;     // space between keyboard and mouse
plate_pad = 4.0;     // plate margin around them

// ---- keyboard ---------------------------------------------------------------
pitch     = 3.6;     // 1u key pitch
key_gap   = 0.6;
key_h     = 0.8;     // key cap height above the case top
key_r     = 0.5;
kb_margin = 1.6;     // case margin around the keys
kb_front_h = 1.6;    // case height at the front edge
kb_back_h  = 2.6;    // ...and at the back (the usual wedge)
numpad_gap = 0.5;    // in key units

// key rows, in key units (widths). Main block is 13u wide, 4 rows (no number row).
rows = [
    [1,1,1,1,1,1,1,1,1,1,1,2],
    [1.5,1,1,1,1,1,1,1,1,1,1,1.5],
    [1.75,1,1,1,1,1,1,1,1,1,2.25],
    [1.5,1.5,6,1.5,1.5,1]
];
main_u   = 13;
num_cols = 3;
num_rows = len(rows);

kb_w = (main_u + numpad_gap + num_cols) * pitch + 2*kb_margin;
kb_d = num_rows * pitch + 2*kb_margin;

// ---- mouse ------------------------------------------------------------------
mouse_w = 11.0;   // X
mouse_l = 19.0;   // Y
mouse_h = 4.2;   // flat

// ---- layout -----------------------------------------------------------------
plate_w = kb_w + plate_gap + mouse_w + 2*plate_pad;
plate_d = max(kb_d, mouse_l) + 2*plate_pad;

// ============================================================================
module rounded_rect(w, l, r) {
    hull() for (x = [-1, 1], y = [-1, 1])
        translate([x*(w/2 - r), y*(l/2 - r)]) circle(r = r);
}

module keycap(w, d) {
    // slightly proud, soft-cornered cap; sinks 0.3 mm into the case so it is fused
    translate([0, 0, -0.3]) linear_extrude(height = key_h + 0.3)
        rounded_rect(w, d, key_r);
}

// ---- keyboard: wedge case + tilted key field ----------------------------------
tilt_a = atan((kb_back_h - kb_front_h) / kb_d);

module kb_case() {
    // wedge (low at the front, y = -kb_d/2), soft vertical corners
    hull() {
        for (x = [-1, 1])
            translate([x*(kb_w/2 - 1.5), 0, 0]) {
                translate([0, -kb_d/2 + 1.5, 0]) cylinder(r = 1.5, h = kb_front_h);
                translate([0,  kb_d/2 - 1.5, 0]) cylinder(r = 1.5, h = kb_back_h);
            }
    }
}

module kb_keys() {
    // origin at the front-left corner of the key field
    for (r = [0 : num_rows - 1]) {
        widths = rows[num_rows - 1 - r];       // row 0 = bottom (space bar) row, at the front
        y = r * pitch;
        // main block
        x = 0;
        for (i = [0 : len(widths) - 1]) {
            xo = sum_to(widths, i) * pitch;
            translate([xo + widths[i]*pitch/2, y + pitch/2, 0])
                keycap(widths[i]*pitch - key_gap, pitch - key_gap);
        }
        // numpad: 4 columns of 1u keys
        for (c = [0 : num_cols - 1])
            translate([(main_u + numpad_gap + c)*pitch + pitch/2, y + pitch/2, 0])
                keycap(pitch - key_gap, pitch - key_gap);
    }
}

function sum_to(v, n) = n == 0 ? 0 : v[n-1] + sum_to(v, n-1);

module keyboard() {
    kb_case();
    // key field sits on the sloped top: tilt about the front edge of the top surface
    translate([-(kb_w/2 - kb_margin), -kb_d/2 + kb_margin, kb_front_h - 0.05 + (kb_back_h - kb_front_h) * kb_margin / kb_d])
        rotate([tilt_a, 0, 0])
            kb_keys();
}

// ---- mouse: domed body, split buttons, scroll wheel --------------------------
module mouse() {
    difference() {
        intersection() {
            // two domes hulled: straighter flanks than a single ellipsoid (rear a touch wider and taller)
            hull() {
                translate([0, -mouse_l/2 + 7.0, 0]) scale([mouse_w/2, 7.0, mouse_h]) sphere(r = 1, $fn = 64);
                translate([0,  mouse_l/2 - 5.5, 0]) scale([mouse_w/2 - 0.5, 5.5, mouse_h - 0.6]) sphere(r = 1, $fn = 64);
            }
            translate([-50, -50, 0]) cube([100, 100, 50]);
        }
        // left/right button split, front half
        translate([-0.2, 1.0, mouse_h*0.7]) cube([0.4, mouse_l/2, 10]);
        // button/body seam across the front third
        translate([-mouse_w/2, 1.0, mouse_h*0.72]) cube([mouse_w, 0.35, 4]);
    }
    // scroll wheel, sitting in the split
    translate([0, 2.8, mouse_h - 1.0])
        rotate([0, 90, 0]) cylinder(d = 2.0, h = 1.2, center = true);
}

// ============================================================================
module piece() {
    // plate
    linear_extrude(height = plate_t) rounded_rect(plate_w, plate_d, plate_r);

    // keyboard on the left, mouse on the right, both centred front-to-back
    translate([-plate_w/2 + plate_pad + kb_w/2, 0, plate_t - 0.01]) keyboard();
    translate([ plate_w/2 - plate_pad - mouse_w/2, 0, plate_t - 0.01]) mouse();
}

piece();
