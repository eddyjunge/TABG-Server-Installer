//! A byte-exact equivalent of .NET's `BinaryReader` / `BinaryWriter`.

use thiserror::Error;

#[derive(Debug, Error, Clone, PartialEq, Eq)]
pub enum DecodeError {
    #[error("unexpected end of payload at byte {offset} (needed {needed} more)")]
    Eof { offset: usize, needed: usize },
    #[error("invalid 7-bit encoded length at byte {offset}")]
    BadLength { offset: usize },
    #[error("invalid UTF-8 string at byte {offset}")]
    BadUtf8 { offset: usize },
    #[error("negative length {len} at byte {offset}")]
    NegativeLength { offset: usize, len: i32 },
    #[error("{remaining} unexpected trailing bytes")]
    Trailing { remaining: usize },
}

/// Reads a payload the way `BinaryReader` does.
#[derive(Clone, Debug)]
pub struct Reader<'a> {
    buf: &'a [u8],
    pos: usize,
}

impl<'a> Reader<'a> {
    pub fn new(buf: &'a [u8]) -> Self {
        Self { buf, pos: 0 }
    }

    pub fn position(&self) -> usize {
        self.pos
    }

    pub fn remaining(&self) -> usize {
        self.buf.len() - self.pos
    }

    pub fn is_empty(&self) -> bool {
        self.remaining() == 0
    }

    /// Error unless the whole payload was consumed.
    pub fn finish(&self) -> Result<(), DecodeError> {
        match self.remaining() {
            0 => Ok(()),
            remaining => Err(DecodeError::Trailing { remaining }),
        }
    }

    /// Like [`Reader::finish`], but trailing zero bytes are allowed. The official server sizes some
    /// buffers from an estimate and sends the unused zero tail (e.g. the world state).
    pub fn finish_allow_zero_padding(&self) -> Result<(), DecodeError> {
        match self.buf[self.pos..].iter().filter(|&&b| b != 0).count() {
            0 => Ok(()),
            _ => Err(DecodeError::Trailing {
                remaining: self.remaining(),
            }),
        }
    }

    pub fn bytes(&mut self, n: usize) -> Result<&'a [u8], DecodeError> {
        if self.remaining() < n {
            return Err(DecodeError::Eof {
                offset: self.pos,
                needed: n - self.remaining(),
            });
        }
        let out = &self.buf[self.pos..self.pos + n];
        self.pos += n;
        Ok(out)
    }

    fn array<const N: usize>(&mut self) -> Result<[u8; N], DecodeError> {
        Ok(self.bytes(N)?.try_into().expect("length checked"))
    }

    pub fn u8(&mut self) -> Result<u8, DecodeError> {
        Ok(self.array::<1>()?[0])
    }
    /// `ReadBoolean`: any non-zero byte is `true`.
    pub fn bool(&mut self) -> Result<bool, DecodeError> {
        Ok(self.u8()? != 0)
    }
    pub fn u16(&mut self) -> Result<u16, DecodeError> {
        Ok(u16::from_le_bytes(self.array()?))
    }
    pub fn i32(&mut self) -> Result<i32, DecodeError> {
        Ok(i32::from_le_bytes(self.array()?))
    }
    pub fn u32(&mut self) -> Result<u32, DecodeError> {
        Ok(u32::from_le_bytes(self.array()?))
    }
    pub fn u64(&mut self) -> Result<u64, DecodeError> {
        Ok(u64::from_le_bytes(self.array()?))
    }
    pub fn f32(&mut self) -> Result<f32, DecodeError> {
        Ok(f32::from_le_bytes(self.array()?))
    }
    pub fn vec3(&mut self) -> Result<[f32; 3], DecodeError> {
        Ok([self.f32()?, self.f32()?, self.f32()?])
    }

    /// `Read7BitEncodedInt`, as used for string lengths.
    pub fn len7(&mut self) -> Result<usize, DecodeError> {
        let start = self.pos;
        let mut value: u32 = 0;
        for i in 0..5 {
            let b = self.u8()?;
            value |= u32::from(b & 0x7f) << (7 * i);
            if b & 0x80 == 0 {
                return i32::try_from(value)
                    .map(|v| v as usize)
                    .map_err(|_| DecodeError::BadLength { offset: start });
            }
        }
        Err(DecodeError::BadLength { offset: start })
    }

    /// `ReadString`: 7-bit length prefix, then UTF-8.
    pub fn string(&mut self) -> Result<String, DecodeError> {
        let len = self.len7()?;
        let offset = self.pos;
        let raw = self.bytes(len)?;
        String::from_utf8(raw.to_vec()).map_err(|_| DecodeError::BadUtf8 { offset })
    }

    /// An `int32` length followed by that many raw bytes (TABG writes names this way).
    pub fn i32_prefixed_bytes(&mut self) -> Result<&'a [u8], DecodeError> {
        let offset = self.pos;
        let len = self.i32()?;
        let len = usize::try_from(len).map_err(|_| DecodeError::NegativeLength { offset, len })?;
        self.bytes(len)
    }

    /// An `int32`-length-prefixed UTF-8 string.
    pub fn i32_prefixed_string(&mut self) -> Result<String, DecodeError> {
        let offset = self.pos + 4;
        let raw = self.i32_prefixed_bytes()?;
        String::from_utf8(raw.to_vec()).map_err(|_| DecodeError::BadUtf8 { offset })
    }

    /// An `int32` count followed by that many `int32`s.
    pub fn i32_array(&mut self) -> Result<Vec<i32>, DecodeError> {
        let offset = self.pos;
        let len = self.i32()?;
        let len = usize::try_from(len).map_err(|_| DecodeError::NegativeLength { offset, len })?;
        if self.remaining() < len.saturating_mul(4) {
            return Err(DecodeError::Eof {
                offset: self.pos,
                needed: len * 4 - self.remaining(),
            });
        }
        (0..len).map(|_| self.i32()).collect()
    }
}

/// Writes a payload the way `BinaryWriter` does.
#[derive(Clone, Debug, Default)]
pub struct Writer {
    buf: Vec<u8>,
}

impl Writer {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn into_inner(self) -> Vec<u8> {
        self.buf
    }

    pub fn bytes(&mut self, b: &[u8]) -> &mut Self {
        self.buf.extend_from_slice(b);
        self
    }
    pub fn u8(&mut self, v: u8) -> &mut Self {
        self.buf.push(v);
        self
    }
    pub fn bool(&mut self, v: bool) -> &mut Self {
        self.u8(u8::from(v))
    }
    pub fn u16(&mut self, v: u16) -> &mut Self {
        self.bytes(&v.to_le_bytes())
    }
    pub fn i32(&mut self, v: i32) -> &mut Self {
        self.bytes(&v.to_le_bytes())
    }
    pub fn u32(&mut self, v: u32) -> &mut Self {
        self.bytes(&v.to_le_bytes())
    }
    pub fn u64(&mut self, v: u64) -> &mut Self {
        self.bytes(&v.to_le_bytes())
    }
    pub fn f32(&mut self, v: f32) -> &mut Self {
        self.bytes(&v.to_le_bytes())
    }
    pub fn vec3(&mut self, v: [f32; 3]) -> &mut Self {
        self.f32(v[0]).f32(v[1]).f32(v[2])
    }

    pub fn len7(&mut self, len: usize) -> &mut Self {
        let mut v = u32::try_from(len).expect("string too long for 7-bit length");
        while v >= 0x80 {
            self.u8((v as u8) | 0x80);
            v >>= 7;
        }
        self.u8(v as u8)
    }

    pub fn string(&mut self, s: &str) -> &mut Self {
        self.len7(s.len()).bytes(s.as_bytes())
    }

    pub fn i32_prefixed_bytes(&mut self, b: &[u8]) -> &mut Self {
        self.i32(i32::try_from(b.len()).expect("too long")).bytes(b)
    }

    pub fn i32_prefixed_string(&mut self, s: &str) -> &mut Self {
        self.i32_prefixed_bytes(s.as_bytes())
    }

    pub fn i32_array(&mut self, values: &[i32]) -> &mut Self {
        self.i32(i32::try_from(values.len()).expect("too long"));
        for &v in values {
            self.i32(v);
        }
        self
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn seven_bit_lengths_match_dotnet() {
        // BinaryWriter.Write(string) prefixes: 0 -> 00, 127 -> 7F, 128 -> 80 01, 300 -> AC 02.
        for (len, expected) in [
            (0usize, vec![0x00]),
            (127, vec![0x7f]),
            (128, vec![0x80, 0x01]),
            (300, vec![0xac, 0x02]),
        ] {
            let mut w = Writer::new();
            w.len7(len);
            let bytes = w.into_inner();
            assert_eq!(bytes, expected, "len {len}");
            assert_eq!(Reader::new(&bytes).len7().unwrap(), len);
        }
    }

    #[test]
    fn round_trips_primitives_and_strings() {
        let mut w = Writer::new();
        w.u8(7)
            .bool(true)
            .u16(0xbeef)
            .i32(-5)
            .u64(u64::MAX)
            .f32(1.5)
            .string("Jönas")
            .i32_prefixed_string("abc")
            .i32_array(&[1, -2]);
        let bytes = w.into_inner();
        let mut r = Reader::new(&bytes);
        assert_eq!(r.u8().unwrap(), 7);
        assert!(r.bool().unwrap());
        assert_eq!(r.u16().unwrap(), 0xbeef);
        assert_eq!(r.i32().unwrap(), -5);
        assert_eq!(r.u64().unwrap(), u64::MAX);
        assert_eq!(r.f32().unwrap(), 1.5);
        assert_eq!(r.string().unwrap(), "Jönas");
        assert_eq!(r.i32_prefixed_string().unwrap(), "abc");
        assert_eq!(r.i32_array().unwrap(), vec![1, -2]);
        r.finish().unwrap();
    }

    #[test]
    fn truncated_input_is_an_error_not_a_panic() {
        assert!(matches!(
            Reader::new(&[1, 2]).i32(),
            Err(DecodeError::Eof { .. })
        ));
        assert!(Reader::new(&[0xff, 0xff, 0xff, 0xff, 0xff]).len7().is_err());
        assert!(Reader::new(&[0xff, 0xff, 0xff, 0x7f]).i32_array().is_err());
    }
}
