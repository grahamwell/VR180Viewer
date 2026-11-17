"""
Copy the camera's tilt reading into JPEGs exported by QooCam Studio, so the VR180 Viewer can level them.

    python qoocam_tag_studio.py  <QooCam Studio output folder>  <folder with the camera's own files>

QooCam Studio drops the camera's tilt sensor reading (IMUHEX) and serial number from the files it
exports. For each Studio JPEG this script finds the camera original with the same base name
(Q360_20261113_133257_000001_anything.jpg -> Q360_20261113_133257_000001.JPG, or .DNG if there is
no JPG), reads IMUHEX and SN from it, and inserts them into the Studio file as a JPEG comment.

  * EXPORT FROM STUDIO WITH HORIZON CORRECTION OFF. The viewer levels a tagged photo from the tilt
    reading; a photo Studio has already levelled would be turned twice. (For a single photo that
    went wrong, untick "Level the horizon" in the viewer's adjustments panel.)
  * The picture itself is not touched: no re-compression, the image data is copied byte for byte.
    The file grows by about 100 bytes and keeps its modified time.
  * Files are changed in place. Running it again is harmless: tagged files are skipped.
  * It does NOT set STITCHED=0. That marks an unprocessed two-circle photo, and the viewer would
    try to un-fisheye a picture that Studio has already stitched.
  * Options:  --dry-run  (report only)   --recursive  (include subfolders of the output folder)

Needs Python 3.8 or later, nothing else.
"""
import os, re, struct, sys

HEAD = 256 * 1024          # where the camera's metadata can be (it's in the first 16 KB of a JPG or DNG)
VIEWER_HEAD = 64 * 1024    # the viewer only reads this much of a file
MARK = 'VR180TILT=1'       # says: added by this script


def read_head(path, n=HEAD):
    with open(path, 'rb') as f:
        return f.read(n)


def camera_tilt(path):
    """IMUHEX (36 hex digits) and serial number from a camera JPG or DNG, or (None, None)"""
    text = read_head(path).decode('latin-1')
    if 'PRODUCT=QooCam' not in text:
        return None, None
    imu = re.search(r'IMUHEX=([0-9A-Fa-f]{36})', text)
    sn = re.search(r'\bSN=([A-Za-z0-9]+)', text)
    return (imu.group(1) if imu else None), (sn.group(1) if sn else None)


def tilt_degrees(imuhex):
    """camera pitch (+ = up) and roll from the accelerometer: values 3..5 of nine little-endian int16"""
    import math
    v = struct.unpack('<9h', bytes.fromhex(imuhex))
    ax, ay, az = v[3:6]
    g = math.sqrt(ax * ax + ay * ay + az * az)
    if not 1500 < g < 2600:          # 2048 per g; the viewer ignores readings outside this range too
        return None
    return math.degrees(math.atan2(-az, -ay)), math.degrees(math.atan2(ax, -ay))


def insert_point(data):
    """Offset just after the leading APPn segments of a JPEG (or after APP0 if they run past the
    part of the file the viewer reads)."""
    if data[:2] != b'\xff\xd8':
        raise ValueError('not a JPEG')
    i, after_app0 = 2, 2
    while i + 4 <= len(data) and data[i] == 0xFF and 0xE0 <= data[i + 1] <= 0xEF:
        i += 2 + struct.unpack('>H', data[i + 2:i + 4])[0]
        if after_app0 == 2:
            after_app0 = i
    if i + 4 > len(data):
        raise ValueError('header segments run past the first %d KB' % (HEAD // 1024))
    return i if i < VIEWER_HEAD - 1024 else after_app0


def originals_index(folder):
    """base name (upper case) -> path, preferring .JPG over .DNG; the camera's small previews are ignored"""
    index = {}
    for name in os.listdir(folder):
        stem, ext = os.path.splitext(name)
        if ext.lower() not in ('.jpg', '.jpeg', '.dng') or 'thumbstitched' in name.lower():
            continue
        key = stem.upper()
        if key not in index or ext.lower() != '.dng':
            index[key] = os.path.join(folder, name)
    return index


def find_original(path, index):
    """The camera original for a Studio file: the file whose name is the longest leading part of this
    name (cut at underscores) and which carries a tilt reading. Returns (path, imuhex, serial) or None.
    The two folders may be the same one: the Studio file itself and other exports are passed over."""
    parts = os.path.splitext(os.path.basename(path))[0].upper().split('_')
    for n in range(len(parts), 0, -1):
        hit = index.get('_'.join(parts[:n]))
        if hit and os.path.abspath(hit) != os.path.abspath(path):
            imu, sn = camera_tilt(hit)
            if imu:
                return hit, imu, sn
    return None


def main(argv):
    flags = [a for a in argv if a.startswith('--')]
    args = [a for a in argv if not a.startswith('--')]
    if len(args) != 2 or any(f not in ('--dry-run', '--recursive') for f in flags):
        print(__doc__)
        return 2
    out_dir, cam_dir = args
    dry, recursive = '--dry-run' in flags, '--recursive' in flags
    index = originals_index(cam_dir)
    print(f'{len(index)} JPG/DNG files to match against in {cam_dir}')

    files = []
    for root, dirs, names in os.walk(out_dir):
        files += [os.path.join(root, n) for n in sorted(names) if n.lower().endswith(('.jpg', '.jpeg'))]
        if not recursive:
            break

    counts = {'tagged': 0, 'already tagged': 0, 'not a Studio export': 0, 'no original found': 0,
              'tilt reading unusable': 0, 'failed': 0}
    for path in files:
        name = os.path.basename(path)
        try:
            head = read_head(path)
            text = head[:VIEWER_HEAD].decode('latin-1')
            if 'PRODUCT=QooCam' not in text or 'STITCHED=1' not in text:
                counts['not a Studio export'] += 1      # includes the camera's own JPEGs (STITCHED=0)
                continue
            if 'IMUHEX=' in text:
                counts['already tagged'] += 1
                continue
            found = find_original(path, index)
            if found is None:
                counts['no original found'] += 1
                print(f'  {name}: no camera original with a matching name and a tilt reading')
                continue
            original, imu, sn = found
            tilt = tilt_degrees(imu)
            if tilt is None:
                counts['tilt reading unusable'] += 1
                print(f'  {name}: the tilt reading in {os.path.basename(original)} is not about 1 g (camera moving?)')
                continue
            comment = f'{MARK} PRODUCT=QooCam STITCHED=1 IMUHEX={imu}' + (f' SN={sn}' if sn else '') + ' '
            segment = b'\xff\xfe' + struct.pack('>H', len(comment) + 2) + comment.encode('ascii')
            at = insert_point(head)
            print(f'  {name}: camera {abs(tilt[0]):.1f} deg {"up" if tilt[0] >= 0 else "down"}, '
                  f'roll {tilt[1]:+.1f} deg  (from {os.path.basename(original)})')
            if not dry:
                st = os.stat(path)
                tmp = path + '.tagging'
                with open(path, 'rb') as src, open(tmp, 'wb') as dst:
                    dst.write(src.read(at))
                    dst.write(segment)
                    while True:
                        block = src.read(8 * 1024 * 1024)
                        if not block:
                            break
                        dst.write(block)
                if os.path.getsize(tmp) != st.st_size + len(segment):
                    os.remove(tmp)
                    raise IOError('the copy came out the wrong size; original left as it was')
                os.replace(tmp, path)
                os.utime(path, ns=(st.st_atime_ns, st.st_mtime_ns))
            counts['tagged'] += 1
        except Exception as e:                           # carry on with the other files
            counts['failed'] += 1
            print(f'  {name}: FAILED: {e}')

    print(('Dry run. Would tag: ' if dry else 'Done. ') + ', '.join(f'{k}: {v}' for k, v in counts.items() if v or k == 'tagged'))
    return 0 if not counts['failed'] else 1


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
