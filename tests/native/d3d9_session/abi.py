"""ctypes declarations of native/Mmd.Modern.D3D9.Compatibility/mda_d3d9_session.h.

This module is import-safe: it never loads a DLL or creates a graphics device.
"""
import ctypes as C

VERSION = 1
OK, INVALID_ARGUMENT, ABI_MISMATCH, INVALID_HANDLE = 0, -1, -2, -3
WRONG_THREAD, BUSY, UNSUPPORTED, PACKET_INVALID, BUFFER_TOO_SMALL = -4, -5, -6, -7, -8


class Error(C.Structure):
    _fields_ = [('size', C.c_uint32), ('version', C.c_uint32),
                ('utf8', C.c_void_p), ('capacity', C.c_uint64),
                ('requiredBytes', C.c_uint64), ('status', C.c_int32),
                ('reserved', C.c_uint32)]


class Abi(C.Structure):
    _fields_ = [('size', C.c_uint32), ('version', C.c_uint32),
                ('abiVersion', C.c_uint32), ('pointerBits', C.c_uint32),
                ('maxWidth', C.c_uint32), ('maxHeight', C.c_uint32),
                ('supportedFeatures', C.c_uint64), ('reserved', C.c_uint32 * 2)]


class Create(C.Structure):
    _fields_ = [('size', C.c_uint32), ('version', C.c_uint32),
                ('width', C.c_uint32), ('height', C.c_uint32),
                ('adapterOrdinal', C.c_uint32), ('reserved', C.c_uint32)]


class Resize(C.Structure):
    _fields_ = [('size', C.c_uint32), ('version', C.c_uint32),
                ('width', C.c_uint32), ('height', C.c_uint32)]


class Capture(C.Structure):
    _fields_ = [('size', C.c_uint32), ('version', C.c_uint32),
                ('frameBytes', C.c_void_p), ('frameLength', C.c_uint64),
                ('visualBytes', C.c_void_p), ('visualLength', C.c_uint64),
                ('requiredFeatures', C.c_uint64), ('flags', C.c_uint32),
                ('renderOrder', C.c_uint32), ('width', C.c_uint32),
                ('height', C.c_uint32), ('rgba', C.c_void_p),
                ('rgbaCapacity', C.c_uint64), ('rgbaStride', C.c_uint64)]


class Result(C.Structure):
    _fields_ = [('size', C.c_uint32), ('version', C.c_uint32),
                ('width', C.c_uint32), ('height', C.c_uint32),
                ('sourceFrame', C.c_double), ('completedCaptures', C.c_uint64),
                ('geometryGeneration', C.c_uint64), ('rgbaBytesRequired', C.c_uint64),
                ('drawCalls', C.c_uint32), ('flags', C.c_uint32),
                ('effectDispatches', C.c_uint32), ('effectPassDraws', C.c_uint32),
                ('effectPassFailures', C.c_uint32), ('rawFallbackDraws', C.c_uint32),
                ('noUiDiagnostics', C.c_uint32), ('reserved', C.c_uint32)]


def init(kind, **fields):
    value = kind()
    value.size = C.sizeof(kind)
    value.version = VERSION
    for key, field in fields.items():
        setattr(value, key, field)
    return value


def load(path):
    """Load an explicitly requested cdecl Windows x64 DLL; no discovery/fallback."""
    if C.sizeof(C.c_void_p) != 8:
        raise RuntimeError('The native ABI requires a 64-bit Python process')
    library = C.CDLL(str(path.resolve()))
    signatures = {
        'QueryAbi': [C.POINTER(Abi), C.POINTER(Error)],
        'Create': [C.POINTER(Create), C.POINTER(C.c_uint64), C.POINTER(Error)],
        'Capture': [C.c_uint64, C.POINTER(Capture), C.POINTER(Result), C.POINTER(Error)],
        'Resize': [C.c_uint64, C.POINTER(Resize), C.POINTER(Error)],
        'Destroy': [C.c_uint64, C.POINTER(Error)],
    }
    for name, arguments in signatures.items():
        function = getattr(library, 'MdaD3D9' + name + 'V1')
        function.argtypes, function.restype = arguments, C.c_int32
        setattr(library, name, function)
    return library


def sizes():
    actual = {kind.__name__: C.sizeof(kind)
              for kind in (Error, Abi, Create, Resize, Capture, Result)}
    expected = dict(Error=40, Abi=40, Create=24, Resize=16, Capture=88, Result=80)
    if actual != expected:
        raise RuntimeError(f'ctypes does not match the Windows x64 ABI: {actual}')
    return actual
