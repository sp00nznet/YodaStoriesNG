#!/usr/bin/env python3
"""Decompress SZDD (Microsoft compress) files"""
import struct
import sys

def decompress_szdd(input_path, output_path):
    with open(input_path, 'rb') as f:
        # Read header
        magic = f.read(8)
        if magic[:4] != b'SZDD':
            print(f"Not an SZDD file: {magic[:4]}")
            return False

        # Get compression mode and missing char
        comp_mode = magic[4]
        missing_char = magic[5]

        # Read uncompressed size (4 bytes, little-endian)
        uncompressed_size = struct.unpack('<I', f.read(4))[0]
        print(f"Uncompressed size: {uncompressed_size}")

        # Read compressed data
        compressed = f.read()

    # Decompress using LZ77 variant
    output = bytearray()
    ring_buffer = bytearray(4096)
    ring_pos = 4096 - 16

    i = 0
    while i < len(compressed) and len(output) < uncompressed_size:
        flags = compressed[i]
        i += 1

        for bit in range(8):
            if i >= len(compressed) or len(output) >= uncompressed_size:
                break

            if flags & (1 << bit):
                # Literal byte
                output.append(compressed[i])
                ring_buffer[ring_pos] = compressed[i]
                ring_pos = (ring_pos + 1) & 0xFFF
                i += 1
            else:
                # Back reference
                if i + 1 >= len(compressed):
                    break
                b1 = compressed[i]
                b2 = compressed[i + 1]
                i += 2

                offset = b1 | ((b2 & 0xF0) << 4)
                length = (b2 & 0x0F) + 3

                for _ in range(length):
                    if len(output) >= uncompressed_size:
                        break
                    byte = ring_buffer[offset]
                    output.append(byte)
                    ring_buffer[ring_pos] = byte
                    ring_pos = (ring_pos + 1) & 0xFFF
                    offset = (offset + 1) & 0xFFF

    with open(output_path, 'wb') as f:
        f.write(output)

    print(f"Decompressed {len(output)} bytes to {output_path}")
    return True

if __name__ == '__main__':
    if len(sys.argv) != 3:
        print("Usage: decompress_szdd.py input.DA_ output.DTA")
        sys.exit(1)

    decompress_szdd(sys.argv[1], sys.argv[2])
