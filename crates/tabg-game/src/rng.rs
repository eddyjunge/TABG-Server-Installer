//! A tiny seedable RNG (SplitMix64) so match logic is deterministic in tests.

#[derive(Clone, Debug)]
pub struct Rng(u64);

impl Rng {
    pub fn new(seed: u64) -> Self {
        Self(seed)
    }

    pub fn next_u64(&mut self) -> u64 {
        self.0 = self.0.wrapping_add(0x9e37_79b9_7f4a_7c15);
        let mut z = self.0;
        z = (z ^ (z >> 30)).wrapping_mul(0xbf58_476d_1ce4_e5b9);
        z = (z ^ (z >> 27)).wrapping_mul(0x94d0_49bb_1331_11eb);
        z ^ (z >> 31)
    }

    /// Uniform in `[0, 1)`.
    pub fn unit(&mut self) -> f32 {
        (self.next_u64() >> 40) as f32 / (1u64 << 24) as f32
    }

    /// Uniform in `[lo, hi)`.
    pub fn range(&mut self, lo: f32, hi: f32) -> f32 {
        lo + (hi - lo) * self.unit()
    }

    /// A uniform point inside the unit circle, like Unity's `Random.insideUnitCircle`.
    pub fn inside_unit_circle(&mut self) -> [f32; 2] {
        loop {
            let (x, y) = (self.range(-1.0, 1.0), self.range(-1.0, 1.0));
            if x * x + y * y <= 1.0 {
                return [x, y];
            }
        }
    }

    pub fn index(&mut self, len: usize) -> usize {
        (self.next_u64() % len.max(1) as u64) as usize
    }
}
