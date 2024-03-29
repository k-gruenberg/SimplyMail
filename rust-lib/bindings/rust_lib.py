# This file was autogenerated by some hot garbage in the `uniffi` crate.
# Trust me, you don't want to mess with it!

# Tell mypy (a type checker) to ignore all errors from this file.
# See https://mypy.readthedocs.io/en/stable/config_file.html?highlight=ignore-errors#confval-ignore_errors
# mypy: ignore-errors

# Common helper code.
#
# Ideally this would live in a separate .py file where it can be unittested etc
# in isolation, and perhaps even published as a re-useable package.
#
# However, it's important that the details of how this helper code works (e.g. the
# way that different builtin types are passed across the FFI) exactly match what's
# expected by the rust code on the other side of the interface. In practice right
# now that means coming from the exact some version of `uniffi` that was used to
# compile the rust component. The easiest way to ensure this is to bundle the Python
# helpers directly inline like we're doing here.

import os
import sys
import ctypes
import enum
import struct
import contextlib
import datetime

# Used for default argument values
DEFAULT = object()


class RustBuffer(ctypes.Structure):
    _fields_ = [
        ("capacity", ctypes.c_int32),
        ("len", ctypes.c_int32),
        ("data", ctypes.POINTER(ctypes.c_char)),
    ]

    @staticmethod
    def alloc(size):
        return rust_call(_UniFFILib.ffi_rust_lib_dab3_rustbuffer_alloc, size)

    @staticmethod
    def reserve(rbuf, additional):
        return rust_call(_UniFFILib.ffi_rust_lib_dab3_rustbuffer_reserve, rbuf, additional)

    def free(self):
        return rust_call(_UniFFILib.ffi_rust_lib_dab3_rustbuffer_free, self)

    def __str__(self):
        return "RustBuffer(capacity={}, len={}, data={})".format(
            self.capacity,
            self.len,
            self.data[0:self.len]
        )

    @contextlib.contextmanager
    def allocWithBuilder():
        """Context-manger to allocate a buffer using a RustBufferBuilder.

        The allocated buffer will be automatically freed if an error occurs, ensuring that
        we don't accidentally leak it.
        """
        builder = RustBufferBuilder()
        try:
            yield builder
        except:
            builder.discard()
            raise

    @contextlib.contextmanager
    def consumeWithStream(self):
        """Context-manager to consume a buffer using a RustBufferStream.

        The RustBuffer will be freed once the context-manager exits, ensuring that we don't
        leak it even if an error occurs.
        """
        try:
            s = RustBufferStream(self)
            yield s
            if s.remaining() != 0:
                raise RuntimeError("junk data left in buffer after consuming")
        finally:
            self.free()


class ForeignBytes(ctypes.Structure):
    _fields_ = [
        ("len", ctypes.c_int32),
        ("data", ctypes.POINTER(ctypes.c_char)),
    ]

    def __str__(self):
        return "ForeignBytes(len={}, data={})".format(self.len, self.data[0:self.len])


class RustBufferStream(object):
    """
    Helper for structured reading of bytes from a RustBuffer
    """

    def __init__(self, rbuf):
        self.rbuf = rbuf
        self.offset = 0

    def remaining(self):
        return self.rbuf.len - self.offset

    def _unpack_from(self, size, format):
        if self.offset + size > self.rbuf.len:
            raise InternalError("read past end of rust buffer")
        value = struct.unpack(format, self.rbuf.data[self.offset:self.offset+size])[0]
        self.offset += size
        return value

    def read(self, size):
        if self.offset + size > self.rbuf.len:
            raise InternalError("read past end of rust buffer")
        data = self.rbuf.data[self.offset:self.offset+size]
        self.offset += size
        return data

    def readI8(self):
        return self._unpack_from(1, ">b")

    def readU8(self):
        return self._unpack_from(1, ">B")

    def readI16(self):
        return self._unpack_from(2, ">h")

    def readU16(self):
        return self._unpack_from(2, ">H")

    def readI32(self):
        return self._unpack_from(4, ">i")

    def readU32(self):
        return self._unpack_from(4, ">I")

    def readI64(self):
        return self._unpack_from(8, ">q")

    def readU64(self):
        return self._unpack_from(8, ">Q")

    def readFloat(self):
        v = self._unpack_from(4, ">f")
        return v

    def readDouble(self):
        return self._unpack_from(8, ">d")


class RustBufferBuilder(object):
    """
    Helper for structured writing of bytes into a RustBuffer.
    """

    def __init__(self):
        self.rbuf = RustBuffer.alloc(16)
        self.rbuf.len = 0

    def finalize(self):
        rbuf = self.rbuf
        self.rbuf = None
        return rbuf

    def discard(self):
        if self.rbuf is not None:
            rbuf = self.finalize()
            rbuf.free()

    @contextlib.contextmanager
    def _reserve(self, numBytes):
        if self.rbuf.len + numBytes > self.rbuf.capacity:
            self.rbuf = RustBuffer.reserve(self.rbuf, numBytes)
        yield None
        self.rbuf.len += numBytes

    def _pack_into(self, size, format, value):
        with self._reserve(size):
            # XXX TODO: I feel like I should be able to use `struct.pack_into` here but can't figure it out.
            for i, byte in enumerate(struct.pack(format, value)):
                self.rbuf.data[self.rbuf.len + i] = byte

    def write(self, value):
        with self._reserve(len(value)):
            for i, byte in enumerate(value):
                self.rbuf.data[self.rbuf.len + i] = byte

    def writeI8(self, v):
        self._pack_into(1, ">b", v)

    def writeU8(self, v):
        self._pack_into(1, ">B", v)

    def writeI16(self, v):
        self._pack_into(2, ">h", v)

    def writeU16(self, v):
        self._pack_into(2, ">H", v)

    def writeI32(self, v):
        self._pack_into(4, ">i", v)

    def writeU32(self, v):
        self._pack_into(4, ">I", v)

    def writeI64(self, v):
        self._pack_into(8, ">q", v)

    def writeU64(self, v):
        self._pack_into(8, ">Q", v)

    def writeFloat(self, v):
        self._pack_into(4, ">f", v)

    def writeDouble(self, v):
        self._pack_into(8, ">d", v)
# A handful of classes and functions to support the generated data structures.
# This would be a good candidate for isolating in its own ffi-support lib.

class InternalError(Exception):
    pass

class RustCallStatus(ctypes.Structure):
    """
    Error runtime.
    """
    _fields_ = [
        ("code", ctypes.c_int8),
        ("error_buf", RustBuffer),
    ]

    # These match the values from the uniffi::rustcalls module
    CALL_SUCCESS = 0
    CALL_ERROR = 1
    CALL_PANIC = 2

    def __str__(self):
        if self.code == RustCallStatus.CALL_SUCCESS:
            return "RustCallStatus(CALL_SUCCESS)"
        elif self.code == RustCallStatus.CALL_ERROR:
            return "RustCallStatus(CALL_ERROR)"
        elif self.code == RustCallStatus.CALL_PANIC:
            return "RustCallStatus(CALL_PANIC)"
        else:
            return "RustCallStatus(<invalid code>)"

def rust_call(fn, *args):
    # Call a rust function
    return rust_call_with_error(None, fn, *args)

def rust_call_with_error(error_ffi_converter, fn, *args):
    # Call a rust function and handle any errors
    #
    # This function is used for rust calls that return Result<> and therefore can set the CALL_ERROR status code.
    # error_ffi_converter must be set to the FfiConverter for the error class that corresponds to the result.
    call_status = RustCallStatus(code=RustCallStatus.CALL_SUCCESS, error_buf=RustBuffer(0, 0, None))

    args_with_error = args + (ctypes.byref(call_status),)
    result = fn(*args_with_error)
    if call_status.code == RustCallStatus.CALL_SUCCESS:
        return result
    elif call_status.code == RustCallStatus.CALL_ERROR:
        if error_ffi_converter is None:
            call_status.error_buf.free()
            raise InternalError("rust_call_with_error: CALL_ERROR, but error_ffi_converter is None")
        else:
            raise error_ffi_converter.lift(call_status.error_buf)
    elif call_status.code == RustCallStatus.CALL_PANIC:
        # When the rust code sees a panic, it tries to construct a RustBuffer
        # with the message.  But if that code panics, then it just sends back
        # an empty buffer.
        if call_status.error_buf.len > 0:
            msg = FfiConverterString.lift(call_status.error_buf)
        else:
            msg = "Unknown rust panic"
        raise InternalError(msg)
    else:
        raise InternalError("Invalid RustCallStatus code: {}".format(
            call_status.code))

# A function pointer for a callback as defined by UniFFI.
# Rust definition `fn(handle: u64, method: u32, args: RustBuffer, buf_ptr: *mut RustBuffer) -> int`
FOREIGN_CALLBACK_T = ctypes.CFUNCTYPE(ctypes.c_int, ctypes.c_ulonglong, ctypes.c_ulong, RustBuffer, ctypes.POINTER(RustBuffer))
# Types conforming to `FfiConverterPrimitive` pass themselves directly over the FFI.
class FfiConverterPrimitive:
    @classmethod
    def lift(cls, value):
        return value

    @classmethod
    def lower(cls, value):
        return value

# Helper class for wrapper types that will always go through a RustBuffer.
# Classes should inherit from this and implement the `read` and `write` static methods.
class FfiConverterRustBuffer:
    @classmethod
    def lift(cls, rbuf):
        with rbuf.consumeWithStream() as stream:
            return cls.read(stream)

    @classmethod
    def lower(cls, value):
        with RustBuffer.allocWithBuilder() as builder:
            cls.write(value, builder)
            return builder.finalize()

# Contains loading, initialization code,
# and the FFI Function declarations in a com.sun.jna.Library.
# This is how we find and load the dynamic library provided by the component.
# For now we just look it up by name.
#
# XXX TODO: This will probably grow some magic for resolving megazording in future.
# E.g. we might start by looking for the named component in `libuniffi.so` and if
# that fails, fall back to loading it separately from `lib${componentName}.so`.

from pathlib import Path

def loadIndirect():
    if sys.platform == "darwin":
        libname = "lib{}.dylib"
    elif sys.platform.startswith("win"):
        # As of python3.8, ctypes does not seem to search $PATH when loading DLLs.
        # We could use `os.add_dll_directory` to configure the search path, but
        # it doesn't feel right to mess with application-wide settings. Let's
        # assume that the `.dll` is next to the `.py` file and load by full path.
        libname = os.path.join(
            os.path.dirname(__file__),
            "{}.dll",
        )
    else:
        # Anything else must be an ELF platform - Linux, *BSD, Solaris/illumos
        libname = "lib{}.so"

    lib = libname.format("uniffi_rust_lib")
    path = str(Path(__file__).parent / lib)
    return ctypes.cdll.LoadLibrary(path)

# A ctypes library to expose the extern-C FFI definitions.
# This is an implementation detail which will be called internally by the public API.

_UniFFILib = loadIndirect()
_UniFFILib.rust_lib_dab3_simply_check_imap.argtypes = (
    RustBuffer,
    ctypes.c_uint16,
    RustBuffer,
    RustBuffer,
    ctypes.POINTER(RustCallStatus),
)
_UniFFILib.rust_lib_dab3_simply_check_imap.restype = None
_UniFFILib.rust_lib_dab3_simply_fetch_inbox_top.argtypes = (
    RustBuffer,
    ctypes.c_uint16,
    RustBuffer,
    RustBuffer,
    ctypes.POINTER(RustCallStatus),
)
_UniFFILib.rust_lib_dab3_simply_fetch_inbox_top.restype = RustBuffer
_UniFFILib.rust_lib_dab3_simply_check_smtp.argtypes = (
    RustBuffer,
    RustBuffer,
    RustBuffer,
    ctypes.POINTER(RustCallStatus),
)
_UniFFILib.rust_lib_dab3_simply_check_smtp.restype = ctypes.c_int8
_UniFFILib.rust_lib_dab3_simply_send_plain_text_email.argtypes = (
    RustBuffer,
    RustBuffer,
    RustBuffer,
    RustBuffer,
    RustBuffer,
    ctypes.POINTER(RustCallStatus),
)
_UniFFILib.rust_lib_dab3_simply_send_plain_text_email.restype = RustBuffer
_UniFFILib.rust_lib_dab3_simply_send_html_email.argtypes = (
    RustBuffer,
    RustBuffer,
    RustBuffer,
    RustBuffer,
    RustBuffer,
    RustBuffer,
    ctypes.POINTER(RustCallStatus),
)
_UniFFILib.rust_lib_dab3_simply_send_html_email.restype = RustBuffer
_UniFFILib.ffi_rust_lib_dab3_rustbuffer_alloc.argtypes = (
    ctypes.c_int32,
    ctypes.POINTER(RustCallStatus),
)
_UniFFILib.ffi_rust_lib_dab3_rustbuffer_alloc.restype = RustBuffer
_UniFFILib.ffi_rust_lib_dab3_rustbuffer_from_bytes.argtypes = (
    ForeignBytes,
    ctypes.POINTER(RustCallStatus),
)
_UniFFILib.ffi_rust_lib_dab3_rustbuffer_from_bytes.restype = RustBuffer
_UniFFILib.ffi_rust_lib_dab3_rustbuffer_free.argtypes = (
    RustBuffer,
    ctypes.POINTER(RustCallStatus),
)
_UniFFILib.ffi_rust_lib_dab3_rustbuffer_free.restype = None
_UniFFILib.ffi_rust_lib_dab3_rustbuffer_reserve.argtypes = (
    RustBuffer,
    ctypes.c_int32,
    ctypes.POINTER(RustCallStatus),
)
_UniFFILib.ffi_rust_lib_dab3_rustbuffer_reserve.restype = RustBuffer

# Public interface members begin here.


class FfiConverterUInt8(FfiConverterPrimitive):
    @staticmethod
    def read(buf):
        return buf.readU8()

    @staticmethod
    def write(value, buf):
        buf.writeU8(value)

class FfiConverterUInt16(FfiConverterPrimitive):
    @staticmethod
    def read(buf):
        return buf.readU16()

    @staticmethod
    def write(value, buf):
        buf.writeU16(value)

class FfiConverterBool:
    @classmethod
    def read(cls, buf):
        return cls.lift(buf.readU8())

    @classmethod
    def write(cls, value, buf):
        buf.writeU8(cls.lower(value))

    @staticmethod
    def lift(value):
        return int(value) != 0

    @staticmethod
    def lower(value):
        return 1 if value else 0

class FfiConverterString:
    @staticmethod
    def read(buf):
        size = buf.readI32()
        if size < 0:
            raise InternalError("Unexpected negative string length")
        utf8Bytes = buf.read(size)
        return utf8Bytes.decode("utf-8")

    @staticmethod
    def write(value, buf):
        utf8Bytes = value.encode("utf-8")
        buf.writeI32(len(utf8Bytes))
        buf.write(utf8Bytes)

    @staticmethod
    def lift(buf):
        with buf.consumeWithStream() as stream:
            return stream.read(stream.remaining()).decode("utf-8")

    @staticmethod
    def lower(value):
        with RustBuffer.allocWithBuilder() as builder:
            builder.write(value.encode("utf-8"))
            return builder.finalize()


class SmtpResponse:

    def __init__(self, severity, category, detail, message):
        self.severity = severity
        self.category = category
        self.detail = detail
        self.message = message

    def __str__(self):
        return "SmtpResponse(severity={}, category={}, detail={}, message={})".format(self.severity, self.category, self.detail, self.message)

    def __eq__(self, other):
        if self.severity != other.severity:
            return False
        if self.category != other.category:
            return False
        if self.detail != other.detail:
            return False
        if self.message != other.message:
            return False
        return True

class FfiConverterTypeSmtpResponse(FfiConverterRustBuffer):
    @staticmethod
    def read(buf):
        return SmtpResponse(
            severity=FfiConverterUInt8.read(buf),
            category=FfiConverterUInt8.read(buf),
            detail=FfiConverterUInt8.read(buf),
            message=FfiConverterString.read(buf),
        )

    @staticmethod
    def write(value, buf):
        FfiConverterUInt8.write(value.severity, buf)
        FfiConverterUInt8.write(value.category, buf)
        FfiConverterUInt8.write(value.detail, buf)
        FfiConverterString.write(value.message, buf)



# ImapError
# We want to define each variant as a nested class that's also a subclass,
# which is tricky in Python.  To accomplish this we're going to create each
# class separated, then manually add the child classes to the base class's
# __dict__.  All of this happens in dummy class to avoid polluting the module
# namespace.
class UniFFIExceptionTmpNamespace:
    class ImapError(Exception):
        pass
    
    class IoError(ImapError):
        def __str__(self):
            return "ImapError.IoError({})".format(repr(super().__str__()))

    ImapError.IoError = IoError
    class TlsHandshakeError(ImapError):
        def __str__(self):
            return "ImapError.TlsHandshakeError({})".format(repr(super().__str__()))

    ImapError.TlsHandshakeError = TlsHandshakeError
    class TlsError(ImapError):
        def __str__(self):
            return "ImapError.TlsError({})".format(repr(super().__str__()))

    ImapError.TlsError = TlsError
    class BadResponse(ImapError):
        def __str__(self):
            return "ImapError.BadResponse({})".format(repr(super().__str__()))

    ImapError.BadResponse = BadResponse
    class NoResponse(ImapError):
        def __str__(self):
            return "ImapError.NoResponse({})".format(repr(super().__str__()))

    ImapError.NoResponse = NoResponse
    class ConnectionLost(ImapError):
        def __str__(self):
            return "ImapError.ConnectionLost({})".format(repr(super().__str__()))

    ImapError.ConnectionLost = ConnectionLost
    class ParseError(ImapError):
        def __str__(self):
            return "ImapError.ParseError({})".format(repr(super().__str__()))

    ImapError.ParseError = ParseError
    class ValidateError(ImapError):
        def __str__(self):
            return "ImapError.ValidateError({})".format(repr(super().__str__()))

    ImapError.ValidateError = ValidateError
    class AppendError(ImapError):
        def __str__(self):
            return "ImapError.AppendError({})".format(repr(super().__str__()))

    ImapError.AppendError = AppendError
    class Nonexhaustive(ImapError):
        def __str__(self):
            return "ImapError.Nonexhaustive({})".format(repr(super().__str__()))

    ImapError.Nonexhaustive = Nonexhaustive
ImapError = UniFFIExceptionTmpNamespace.ImapError
del UniFFIExceptionTmpNamespace


class FfiConverterTypeImapError(FfiConverterRustBuffer):
    @staticmethod
    def read(buf):
        variant = buf.readI32()
        if variant == 1:
            return ImapError.IoError(
                FfiConverterString.read(buf),
            )
        if variant == 2:
            return ImapError.TlsHandshakeError(
                FfiConverterString.read(buf),
            )
        if variant == 3:
            return ImapError.TlsError(
                FfiConverterString.read(buf),
            )
        if variant == 4:
            return ImapError.BadResponse(
                FfiConverterString.read(buf),
            )
        if variant == 5:
            return ImapError.NoResponse(
                FfiConverterString.read(buf),
            )
        if variant == 6:
            return ImapError.ConnectionLost(
                FfiConverterString.read(buf),
            )
        if variant == 7:
            return ImapError.ParseError(
                FfiConverterString.read(buf),
            )
        if variant == 8:
            return ImapError.ValidateError(
                FfiConverterString.read(buf),
            )
        if variant == 9:
            return ImapError.AppendError(
                FfiConverterString.read(buf),
            )
        if variant == 10:
            return ImapError.Nonexhaustive(
                FfiConverterString.read(buf),
            )
        raise InternalError("Raw enum value doesn't match any cases")

    @staticmethod
    def write(value, buf):
        if isinstance(value, ImapError.IoError):
            buf.writeI32(1)
        if isinstance(value, ImapError.TlsHandshakeError):
            buf.writeI32(2)
        if isinstance(value, ImapError.TlsError):
            buf.writeI32(3)
        if isinstance(value, ImapError.BadResponse):
            buf.writeI32(4)
        if isinstance(value, ImapError.NoResponse):
            buf.writeI32(5)
        if isinstance(value, ImapError.ConnectionLost):
            buf.writeI32(6)
        if isinstance(value, ImapError.ParseError):
            buf.writeI32(7)
        if isinstance(value, ImapError.ValidateError):
            buf.writeI32(8)
        if isinstance(value, ImapError.AppendError):
            buf.writeI32(9)
        if isinstance(value, ImapError.Nonexhaustive):
            buf.writeI32(10)



# SmtpError
# We want to define each variant as a nested class that's also a subclass,
# which is tricky in Python.  To accomplish this we're going to create each
# class separated, then manually add the child classes to the base class's
# __dict__.  All of this happens in dummy class to avoid polluting the module
# namespace.
class UniFFIExceptionTmpNamespace:
    class SmtpError(Exception):
        pass
    
    class TransientSmtpError(SmtpError):
        def __str__(self):
            return "SmtpError.TransientSmtpError({})".format(repr(super().__str__()))

    SmtpError.TransientSmtpError = TransientSmtpError
    class PermanentSmtpError(SmtpError):
        def __str__(self):
            return "SmtpError.PermanentSmtpError({})".format(repr(super().__str__()))

    SmtpError.PermanentSmtpError = PermanentSmtpError
    class ResponseParseError(SmtpError):
        def __str__(self):
            return "SmtpError.ResponseParseError({})".format(repr(super().__str__()))

    SmtpError.ResponseParseError = ResponseParseError
    class InternalClientError(SmtpError):
        def __str__(self):
            return "SmtpError.InternalClientError({})".format(repr(super().__str__()))

    SmtpError.InternalClientError = InternalClientError
    class ConnectionError(SmtpError):
        def __str__(self):
            return "SmtpError.ConnectionError({})".format(repr(super().__str__()))

    SmtpError.ConnectionError = ConnectionError
    class NetworkError(SmtpError):
        def __str__(self):
            return "SmtpError.NetworkError({})".format(repr(super().__str__()))

    SmtpError.NetworkError = NetworkError
    class TlsError(SmtpError):
        def __str__(self):
            return "SmtpError.TlsError({})".format(repr(super().__str__()))

    SmtpError.TlsError = TlsError
    class Timeout(SmtpError):
        def __str__(self):
            return "SmtpError.Timeout({})".format(repr(super().__str__()))

    SmtpError.Timeout = Timeout
    class OtherError(SmtpError):
        def __str__(self):
            return "SmtpError.OtherError({})".format(repr(super().__str__()))

    SmtpError.OtherError = OtherError
SmtpError = UniFFIExceptionTmpNamespace.SmtpError
del UniFFIExceptionTmpNamespace


class FfiConverterTypeSmtpError(FfiConverterRustBuffer):
    @staticmethod
    def read(buf):
        variant = buf.readI32()
        if variant == 1:
            return SmtpError.TransientSmtpError(
                FfiConverterString.read(buf),
            )
        if variant == 2:
            return SmtpError.PermanentSmtpError(
                FfiConverterString.read(buf),
            )
        if variant == 3:
            return SmtpError.ResponseParseError(
                FfiConverterString.read(buf),
            )
        if variant == 4:
            return SmtpError.InternalClientError(
                FfiConverterString.read(buf),
            )
        if variant == 5:
            return SmtpError.ConnectionError(
                FfiConverterString.read(buf),
            )
        if variant == 6:
            return SmtpError.NetworkError(
                FfiConverterString.read(buf),
            )
        if variant == 7:
            return SmtpError.TlsError(
                FfiConverterString.read(buf),
            )
        if variant == 8:
            return SmtpError.Timeout(
                FfiConverterString.read(buf),
            )
        if variant == 9:
            return SmtpError.OtherError(
                FfiConverterString.read(buf),
            )
        raise InternalError("Raw enum value doesn't match any cases")

    @staticmethod
    def write(value, buf):
        if isinstance(value, SmtpError.TransientSmtpError):
            buf.writeI32(1)
        if isinstance(value, SmtpError.PermanentSmtpError):
            buf.writeI32(2)
        if isinstance(value, SmtpError.ResponseParseError):
            buf.writeI32(3)
        if isinstance(value, SmtpError.InternalClientError):
            buf.writeI32(4)
        if isinstance(value, SmtpError.ConnectionError):
            buf.writeI32(5)
        if isinstance(value, SmtpError.NetworkError):
            buf.writeI32(6)
        if isinstance(value, SmtpError.TlsError):
            buf.writeI32(7)
        if isinstance(value, SmtpError.Timeout):
            buf.writeI32(8)
        if isinstance(value, SmtpError.OtherError):
            buf.writeI32(9)



class FfiConverterOptionalString(FfiConverterRustBuffer):
    @classmethod
    def write(cls, value, buf):
        if value is None:
            buf.writeU8(0)
            return

        buf.writeU8(1)
        FfiConverterString.write(value, buf)

    @classmethod
    def read(cls, buf):
        flag = buf.readU8()
        if flag == 0:
            return None
        elif flag == 1:
            return FfiConverterString.read(buf)
        else:
            raise InternalError("Unexpected flag byte for optional type")



class FfiConverterMapStringString(FfiConverterRustBuffer):
    @classmethod
    def write(cls, items, buf):
        buf.writeI32(len(items))
        for (key, value) in items.items():
            FfiConverterString.write(key, buf)
            FfiConverterString.write(value, buf)

    @classmethod
    def read(cls, buf):
        count = buf.readI32()
        if count < 0:
            raise InternalError("Unexpected negative map size")

        # It would be nice to use a dict comprehension,
        # but in Python 3.7 and before the evaluation order is not according to spec,
        # so we we're reading the value before the key.
        # This loop makes the order explicit: first reading the key, then the value.
        d = {}
        for i in range(count):
            key = FfiConverterString.read(buf)
            val = FfiConverterString.read(buf)
            d[key] = val
        return d

def simply_check_imap(domain,port,username,password):
    domain = domain
    
    port = int(port)
    
    username = username
    
    password = password
    
    rust_call_with_error(FfiConverterTypeImapError,_UniFFILib.rust_lib_dab3_simply_check_imap,
        FfiConverterString.lower(domain),
        FfiConverterUInt16.lower(port),
        FfiConverterString.lower(username),
        FfiConverterString.lower(password))


def simply_fetch_inbox_top(domain,port,username,password):
    domain = domain
    
    port = int(port)
    
    username = username
    
    password = password
    
    return FfiConverterOptionalString.lift(rust_call_with_error(FfiConverterTypeImapError,_UniFFILib.rust_lib_dab3_simply_fetch_inbox_top,
        FfiConverterString.lower(domain),
        FfiConverterUInt16.lower(port),
        FfiConverterString.lower(username),
        FfiConverterString.lower(password)))



def simply_check_smtp(smtp_server,smtp_username,smtp_password):
    smtp_server = smtp_server
    
    smtp_username = smtp_username
    
    smtp_password = smtp_password
    
    return FfiConverterBool.lift(rust_call_with_error(FfiConverterTypeSmtpError,_UniFFILib.rust_lib_dab3_simply_check_smtp,
        FfiConverterString.lower(smtp_server),
        FfiConverterString.lower(smtp_username),
        FfiConverterString.lower(smtp_password)))



def simply_send_plain_text_email(smtp_server,smtp_username,smtp_password,headers,body):
    smtp_server = smtp_server
    
    smtp_username = smtp_username
    
    smtp_password = smtp_password
    
    headers = dict((k, v) for (k, v) in headers.items())
    
    body = body
    
    return FfiConverterTypeSmtpResponse.lift(rust_call_with_error(FfiConverterTypeSmtpError,_UniFFILib.rust_lib_dab3_simply_send_plain_text_email,
        FfiConverterString.lower(smtp_server),
        FfiConverterString.lower(smtp_username),
        FfiConverterString.lower(smtp_password),
        FfiConverterMapStringString.lower(headers),
        FfiConverterString.lower(body)))



def simply_send_html_email(smtp_server,smtp_username,smtp_password,headers,plain_text_body,html_body):
    smtp_server = smtp_server
    
    smtp_username = smtp_username
    
    smtp_password = smtp_password
    
    headers = dict((k, v) for (k, v) in headers.items())
    
    plain_text_body = plain_text_body
    
    html_body = html_body
    
    return FfiConverterTypeSmtpResponse.lift(rust_call_with_error(FfiConverterTypeSmtpError,_UniFFILib.rust_lib_dab3_simply_send_html_email,
        FfiConverterString.lower(smtp_server),
        FfiConverterString.lower(smtp_username),
        FfiConverterString.lower(smtp_password),
        FfiConverterMapStringString.lower(headers),
        FfiConverterString.lower(plain_text_body),
        FfiConverterString.lower(html_body)))



__all__ = [
    "InternalError",
    "SmtpResponse",
    "simply_check_imap",
    "simply_fetch_inbox_top",
    "simply_check_smtp",
    "simply_send_plain_text_email",
    "simply_send_html_email",
    "ImapError",
    "SmtpError",
]

