// A single component of Bullet's soft-target gather, called from the benchmark only.
#[repr(C)]
pub struct Point {
    x: f32,
    y: f32,
    z: f32,
}

/// The caller pins non-overlapping arrays for the call. Output holds count * 3 floats.
#[no_mangle]
pub unsafe extern "C" fn pack_targets(
    source: *const Point, vertices: usize, ids: *const i32, count: usize, output: *mut f32,
) -> i32 {
    let points = std::slice::from_raw_parts(source, vertices);
    let indices = std::slice::from_raw_parts(ids, count);
    let output = std::slice::from_raw_parts_mut(output, count * 3);
    for (&index, target) in indices.iter().zip(output.chunks_exact_mut(3)) {
        let Some(point) = points.get(index as usize) else { return -1 };
        if !(point.x.is_finite() && point.y.is_finite() && point.z.is_finite()) {
            return -1;
        }
        target.copy_from_slice(&[point.x, point.y, point.z]);
    }
    0
}
