//! The bots' map: a top-surface heightfield (from the dumper's walkability grid). Bots walk on
//! it, path around anything they can't step up (buildings, cliffs, water) and use it for line
//! of sight.

use std::cmp::Ordering;
use std::collections::{BinaryHeap, HashMap};

use crate::rng::Rng;

/// Highest step a bot takes between neighbouring cells (metres per cell).
const MAX_STEP: f32 = 0.7;
/// Eye height above the feet.
pub const EYE: f32 = 1.6;

#[derive(Clone, Debug, PartialEq)]
pub struct NavGrid {
    pub min_x: f32,
    pub min_z: f32,
    pub cell: f32,
    pub width: usize,
    pub depth: usize,
    /// `(height - 100) * 64`, 0 = water / nothing.
    pub heights: Vec<u16>,
}

#[derive(Clone, Copy, PartialEq, Eq)]
struct Node {
    cost: u32,
    index: usize,
}

impl Ord for Node {
    fn cmp(&self, other: &Self) -> Ordering {
        other
            .cost
            .cmp(&self.cost)
            .then(self.index.cmp(&other.index))
    }
}

impl PartialOrd for Node {
    fn partial_cmp(&self, other: &Self) -> Option<Ordering> {
        Some(self.cmp(other))
    }
}

impl NavGrid {
    fn cell_of(&self, x: f32, z: f32) -> Option<(usize, usize)> {
        let cx = ((x - self.min_x) / self.cell).floor();
        let cz = ((z - self.min_z) / self.cell).floor();
        if cx < 0.0 || cz < 0.0 {
            return None;
        }
        let (cx, cz) = (cx as usize, cz as usize);
        (cx < self.width && cz < self.depth).then_some((cx, cz))
    }

    fn raw(&self, cx: usize, cz: usize) -> u16 {
        self.heights[cz * self.width + cx]
    }

    fn height_of_cell(&self, cx: usize, cz: usize) -> Option<f32> {
        let v = self.raw(cx, cz);
        (v != 0).then(|| 100.0 + f32::from(v) / 64.0)
    }

    fn centre(&self, cx: usize, cz: usize) -> [f32; 3] {
        let x = self.min_x + (cx as f32 + 0.5) * self.cell;
        let z = self.min_z + (cz as f32 + 0.5) * self.cell;
        [x, self.height_of_cell(cx, cz).unwrap_or(100.0), z]
    }

    /// Ground (top surface) height at `x, z`; `None` over water or off the map.
    pub fn height(&self, x: f32, z: f32) -> Option<f32> {
        let (cx, cz) = self.cell_of(x, z)?;
        self.height_of_cell(cx, cz)
    }

    /// Can a bot stand here (land, and not a lone spike)?
    pub fn walkable(&self, x: f32, z: f32) -> bool {
        self.height(x, z).is_some()
    }

    fn neighbours(&self, cx: usize, cz: usize) -> impl Iterator<Item = (usize, usize, u32)> + '_ {
        const DIRS: [(i32, i32, u32); 8] = [
            (1, 0, 10),
            (-1, 0, 10),
            (0, 1, 10),
            (0, -1, 10),
            (1, 1, 14),
            (1, -1, 14),
            (-1, 1, 14),
            (-1, -1, 14),
        ];
        let here = self.height_of_cell(cx, cz);
        DIRS.into_iter().filter_map(move |(dx, dz, cost)| {
            let nx = usize::try_from(cx as i32 + dx).ok()?;
            let nz = usize::try_from(cz as i32 + dz).ok()?;
            if nx >= self.width || nz >= self.depth {
                return None;
            }
            let there = self.height_of_cell(nx, nz)?;
            let step = (there - here?).abs();
            let limit = if dx != 0 && dz != 0 {
                MAX_STEP * 1.41
            } else {
                MAX_STEP
            };
            (step <= limit).then_some((nx, nz, cost + (step * 20.0) as u32))
        })
    }

    /// A* from `from` to `to` over walkable cells, visiting at most `budget` cells. Returns
    /// waypoints (every few metres) ending at `to`, or `None` if it's unreachable in budget.
    pub fn path(&self, from: [f32; 3], to: [f32; 3], budget: usize) -> Option<Vec<[f32; 3]>> {
        let start = self.cell_of(from[0], from[2])?;
        let goal = self.cell_of(to[0], to[2])?;
        self.height_of_cell(goal.0, goal.1)?;
        let index = |(x, z): (usize, usize)| z * self.width + x;
        let heuristic = |(x, z): (usize, usize)| {
            let dx = x.abs_diff(goal.0) as u32;
            let dz = z.abs_diff(goal.1) as u32;
            10 * dx.max(dz) + 4 * dx.min(dz)
        };
        let mut open = BinaryHeap::new();
        let mut came: HashMap<usize, usize> = HashMap::new();
        let mut cost: HashMap<usize, u32> = HashMap::new();
        open.push(Node {
            cost: heuristic(start),
            index: index(start),
        });
        cost.insert(index(start), 0);
        let mut visited = 0;
        while let Some(Node { index: current, .. }) = open.pop() {
            let (cx, cz) = (current % self.width, current / self.width);
            if (cx, cz) == goal {
                let mut cells = vec![current];
                let mut at = current;
                while let Some(&prev) = came.get(&at) {
                    cells.push(prev);
                    at = prev;
                }
                cells.reverse();
                // Waypoints where the path turns (straight runs between them stay on the path).
                let xy = |c: usize| ((c % self.width) as i64, (c / self.width) as i64);
                let mut points = Vec::new();
                for w in cells.windows(3) {
                    let (a, b, c) = (xy(w[0]), xy(w[1]), xy(w[2]));
                    if (b.0 - a.0, b.1 - a.1) != (c.0 - b.0, c.1 - b.1) {
                        points.push(self.centre(b.0 as usize, b.1 as usize));
                    }
                }
                points.push(self.centre(goal.0, goal.1));
                return Some(points);
            }
            visited += 1;
            if visited > budget {
                return None;
            }
            let g = cost[&current];
            for (nx, nz, step) in self.neighbours(cx, cz) {
                let next = index((nx, nz));
                let candidate = g + step;
                if cost.get(&next).is_none_or(|&c| candidate < c) {
                    cost.insert(next, candidate);
                    came.insert(next, current);
                    open.push(Node {
                        cost: candidate + heuristic((nx, nz)),
                        index: next,
                    });
                }
            }
        }
        None
    }

    /// Can someone with eyes at `from` see `to` (both feet positions)? Anything on the grid
    /// higher than the sight line in between blocks it.
    pub fn line_of_sight(&self, from: [f32; 3], to: [f32; 3]) -> bool {
        let (a, b) = (
            [from[0], from[1] + EYE, from[2]],
            [to[0], to[1] + EYE * 0.75, to[2]],
        );
        let dist = (b[0] - a[0]).hypot(b[2] - a[2]);
        let steps = (dist / self.cell).ceil().max(1.0) as usize;
        for i in 1..steps {
            let t = i as f32 / steps as f32;
            let (x, y, z) = (
                a[0] + (b[0] - a[0]) * t,
                a[1] + (b[1] - a[1]) * t,
                a[2] + (b[2] - a[2]) * t,
            );
            if let Some(h) = self.height(x, z)
                && h > y + 0.2
            {
                return false;
            }
        }
        true
    }

    /// A walkable point within `radius` of `centre` (a few tries), else `None`.
    pub fn random_point_near(
        &self,
        centre: [f32; 3],
        radius: f32,
        rng: &mut Rng,
    ) -> Option<[f32; 3]> {
        for _ in 0..24 {
            let [ox, oz] = rng.inside_unit_circle();
            let (x, z) = (centre[0] + ox * radius, centre[2] + oz * radius);
            if let Some(y) = self.height(x, z) {
                return Some([x, y, z]);
            }
        }
        None
    }

    /// Decode the dumper's file (already gunzipped): `TBNG` v1 header, then u16 heights.
    pub fn decode(bytes: &[u8]) -> Result<Self, String> {
        if bytes.len() < 28 || &bytes[..4] != b"TBNG" {
            return Err("not a tabg walkability grid".into());
        }
        let u32_at = |i: usize| u32::from_le_bytes(bytes[i..i + 4].try_into().expect("4 bytes"));
        let f32_at = |i: usize| f32::from_le_bytes(bytes[i..i + 4].try_into().expect("4 bytes"));
        if u32_at(4) != 1 {
            return Err("unknown walkability grid version".into());
        }
        let (width, depth) = (u32_at(20) as usize, u32_at(24) as usize);
        let body = &bytes[28..];
        if body.len() != width * depth * 2 {
            return Err("walkability grid has the wrong size".into());
        }
        Ok(Self {
            min_x: f32_at(8),
            min_z: f32_at(12),
            cell: f32_at(16),
            width,
            depth,
            heights: body
                .chunks_exact(2)
                .map(|c| u16::from_le_bytes([c[0], c[1]]))
                .collect(),
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// 20×20 m of ground at 120 m with a 4 m wall at x = 10 (gap at z 16..18) and water at x < 2.
    fn grid() -> NavGrid {
        let (w, d) = (20, 20);
        let mut heights = vec![(20 * 64) as u16; w * d];
        for z in 0..d {
            for x in 0..w {
                if x < 2 {
                    heights[z * w + x] = 0;
                }
                if x == 10 && !(16..18).contains(&z) {
                    heights[z * w + x] = (24 * 64) as u16;
                }
            }
        }
        NavGrid {
            min_x: 0.0,
            min_z: 0.0,
            cell: 1.0,
            width: w,
            depth: d,
            heights,
        }
    }

    #[test]
    fn heights_and_water() {
        let g = grid();
        assert_eq!(g.height(5.5, 5.5), Some(120.0));
        assert_eq!(g.height(0.5, 5.5), None, "water");
        assert_eq!(g.height(-1.0, 5.0), None, "off the map");
    }

    #[test]
    fn paths_go_through_the_gap() {
        let g = grid();
        let path = g
            .path([5.5, 120.0, 2.5], [15.5, 120.0, 2.5], 10_000)
            .expect("reachable");
        assert!(
            path.iter().any(|p| p[2] > 15.0),
            "detours through the gap: {path:?}"
        );
        assert_eq!(*path.last().unwrap(), [15.5, 120.0, 2.5]);
        assert!(
            g.path([5.5, 120.0, 2.5], [0.5, 120.0, 2.5], 10_000)
                .is_none(),
            "water"
        );
    }

    #[test]
    fn walls_block_sight() {
        let g = grid();
        assert!(!g.line_of_sight([5.5, 120.0, 5.5], [15.5, 120.0, 5.5]));
        assert!(
            g.line_of_sight([5.5, 120.0, 17.0], [15.5, 120.0, 17.0]),
            "through the gap"
        );
    }

    #[test]
    fn decodes_the_dump_format() {
        let mut bytes = b"TBNG".to_vec();
        bytes.extend_from_slice(&1u32.to_le_bytes());
        for f in [-10.0f32, -20.0, 1.0] {
            bytes.extend_from_slice(&f.to_le_bytes());
        }
        bytes.extend_from_slice(&2u32.to_le_bytes());
        bytes.extend_from_slice(&1u32.to_le_bytes());
        bytes.extend_from_slice(&0u16.to_le_bytes());
        bytes.extend_from_slice(&(64u16 * 30).to_le_bytes());
        let g = NavGrid::decode(&bytes).unwrap();
        assert_eq!(g.height(-8.5, -19.5), Some(130.0));
        assert!(NavGrid::decode(b"nope").is_err());
    }
}
