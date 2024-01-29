


using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
namespace uniffi.rust_lib;



// This is a helper for safely working with byte buffers returned from the Rust code.
// A rust-owned buffer is represented by its capacity, its current length, and a
// pointer to the underlying data.

[StructLayout(LayoutKind.Sequential)]
internal struct RustBuffer {
    public int capacity;
    public int len;
    public IntPtr data;

    public static RustBuffer Alloc(int size) {
        return _UniffiHelpers.RustCall((ref RustCallStatus status) => {
            var buffer = _UniFFILib.ffi_rust_lib_rustbuffer_alloc(size, ref status);
            if (buffer.data == IntPtr.Zero) {
                throw new AllocationException($"RustBuffer.Alloc() returned null data pointer (size={size})");
            }
            return buffer;
        });
    }

    public static void Free(RustBuffer buffer) {
        _UniffiHelpers.RustCall((ref RustCallStatus status) => {
            _UniFFILib.ffi_rust_lib_rustbuffer_free(buffer, ref status);
        });
    }

    public static BigEndianStream MemoryStream(IntPtr data, int length) {
        unsafe {
            return new BigEndianStream(new UnmanagedMemoryStream((byte*)data.ToPointer(), length));
        }
    }

    public BigEndianStream AsStream() {
        unsafe {
            return new BigEndianStream(new UnmanagedMemoryStream((byte*)data.ToPointer(), len));
        }
    }

    public BigEndianStream AsWriteableStream() {
        unsafe {
            return new BigEndianStream(new UnmanagedMemoryStream((byte*)data.ToPointer(), capacity, capacity, FileAccess.Write));
        }
    }
}

// This is a helper for safely passing byte references into the rust code.
// It's not actually used at the moment, because there aren't many things that you
// can take a direct pointer to managed memory, and if we're going to copy something
// then we might as well copy it into a `RustBuffer`. But it's here for API
// completeness.

[StructLayout(LayoutKind.Sequential)]
internal struct ForeignBytes {
    public int length;
    public IntPtr data;
}


// The FfiConverter interface handles converter types to and from the FFI
//
// All implementing objects should be public to support external types.  When a
// type is external we need to import it's FfiConverter.
internal abstract class FfiConverter<CsType, FfiType> {
    // Convert an FFI type to a C# type
    public abstract CsType Lift(FfiType value);

    // Convert C# type to an FFI type
    public abstract FfiType Lower(CsType value);

    // Read a C# type from a `ByteBuffer`
    public abstract CsType Read(BigEndianStream stream);

    // Calculate bytes to allocate when creating a `RustBuffer`
    //
    // This must return at least as many bytes as the write() function will
    // write. It can return more bytes than needed, for example when writing
    // Strings we can't know the exact bytes needed until we the UTF-8
    // encoding, so we pessimistically allocate the largest size possible (3
    // bytes per codepoint).  Allocating extra bytes is not really a big deal
    // because the `RustBuffer` is short-lived.
    public abstract int AllocationSize(CsType value);

    // Write a C# type to a `ByteBuffer`
    public abstract void Write(CsType value, BigEndianStream stream);

    // Lower a value into a `RustBuffer`
    //
    // This method lowers a value into a `RustBuffer` rather than the normal
    // FfiType.  It's used by the callback interface code.  Callback interface
    // returns are always serialized into a `RustBuffer` regardless of their
    // normal FFI type.
    public RustBuffer LowerIntoRustBuffer(CsType value) {
        var rbuf = RustBuffer.Alloc(AllocationSize(value));
        try {
            var stream = rbuf.AsWriteableStream();
            Write(value, stream);
            rbuf.len = Convert.ToInt32(stream.Position);
            return rbuf;
        } catch {
            RustBuffer.Free(rbuf);
            throw;
        }
    }

    // Lift a value from a `RustBuffer`.
    //
    // This here mostly because of the symmetry with `lowerIntoRustBuffer()`.
    // It's currently only used by the `FfiConverterRustBuffer` class below.
    protected CsType LiftFromRustBuffer(RustBuffer rbuf) {
        var stream = rbuf.AsStream();
        try {
           var item = Read(stream);
           if (stream.HasRemaining()) {
               throw new InternalException("junk remaining in buffer after lifting, something is very wrong!!");
           }
           return item;
        } finally {
            RustBuffer.Free(rbuf);
        }
    }
}

// FfiConverter that uses `RustBuffer` as the FfiType
internal abstract class FfiConverterRustBuffer<CsType>: FfiConverter<CsType, RustBuffer> {
    public override CsType Lift(RustBuffer value) {
        return LiftFromRustBuffer(value);
    }
    public override RustBuffer Lower(CsType value) {
        return LowerIntoRustBuffer(value);
    }
}


// A handful of classes and functions to support the generated data structures.
// This would be a good candidate for isolating in its own ffi-support lib.
// Error runtime.
[StructLayout(LayoutKind.Sequential)]
struct RustCallStatus {
    public sbyte code;
    public RustBuffer error_buf;

    public bool IsSuccess() {
        return code == 0;
    }

    public bool IsError() {
        return code == 1;
    }

    public bool IsPanic() {
        return code == 2;
    }
}

// Base class for all uniffi exceptions
public class UniffiException: Exception {
    public UniffiException(): base() {}
    public UniffiException(string message): base(message) {}
}

public class UndeclaredErrorException: UniffiException {
    public UndeclaredErrorException(string message): base(message) {}
}

public class PanicException: UniffiException {
    public PanicException(string message): base(message) {}
}

public class AllocationException: UniffiException {
    public AllocationException(string message): base(message) {}
}

public class InternalException: UniffiException {
    public InternalException(string message): base(message) {}
}

public class InvalidEnumException: InternalException {
    public InvalidEnumException(string message): base(message) {
    }
}

public class UniffiContractVersionException: UniffiException {
    public UniffiContractVersionException(string message): base(message) {
    }
}

public class UniffiContractChecksumException: UniffiException {
    public UniffiContractChecksumException(string message): base(message) {
    }
}

// Each top-level error class has a companion object that can lift the error from the call status's rust buffer
interface CallStatusErrorHandler<E> where E: Exception {
    E Lift(RustBuffer error_buf);
}

// CallStatusErrorHandler implementation for times when we don't expect a CALL_ERROR
class NullCallStatusErrorHandler: CallStatusErrorHandler<UniffiException> {
    public static NullCallStatusErrorHandler INSTANCE = new NullCallStatusErrorHandler();

    public UniffiException Lift(RustBuffer error_buf) {
        RustBuffer.Free(error_buf);
        return new UndeclaredErrorException("library has returned an error not declared in UNIFFI interface file");
    }
}

// Helpers for calling Rust
// In practice we usually need to be synchronized to call this safely, so it doesn't
// synchronize itself
class _UniffiHelpers {
    public delegate void RustCallAction(ref RustCallStatus status);
    public delegate U RustCallFunc<out U>(ref RustCallStatus status);

    // Call a rust function that returns a Result<>.  Pass in the Error class companion that corresponds to the Err
    public static U RustCallWithError<U, E>(CallStatusErrorHandler<E> errorHandler, RustCallFunc<U> callback)
        where E: UniffiException
    {
        var status = new RustCallStatus();
        var return_value = callback(ref status);
        if (status.IsSuccess()) {
            return return_value;
        } else if (status.IsError()) {
            throw errorHandler.Lift(status.error_buf);
        } else if (status.IsPanic()) {
            // when the rust code sees a panic, it tries to construct a rustbuffer
            // with the message.  but if that code panics, then it just sends back
            // an empty buffer.
            if (status.error_buf.len > 0) {
                throw new PanicException(FfiConverterString.INSTANCE.Lift(status.error_buf));
            } else {
                throw new PanicException("Rust panic");
            }
        } else {
            throw new InternalException($"Unknown rust call status: {status.code}");
        }
    }

    // Call a rust function that returns a Result<>.  Pass in the Error class companion that corresponds to the Err
    public static void RustCallWithError<E>(CallStatusErrorHandler<E> errorHandler, RustCallAction callback)
        where E: UniffiException
    {
        _UniffiHelpers.RustCallWithError(errorHandler, (ref RustCallStatus status) => {
            callback(ref status);
            return 0;
        });
    }

    // Call a rust function that returns a plain value
    public static U RustCall<U>(RustCallFunc<U> callback) {
        return _UniffiHelpers.RustCallWithError(NullCallStatusErrorHandler.INSTANCE, callback);
    }

    // Call a rust function that returns a plain value
    public static void RustCall(RustCallAction callback) {
        _UniffiHelpers.RustCall((ref RustCallStatus status) => {
            callback(ref status);
            return 0;
        });
    }
}


// Big endian streams are not yet available in dotnet :'(
// https://github.com/dotnet/runtime/issues/26904

class StreamUnderflowException: Exception {
    public StreamUnderflowException() {
    }
}

class BigEndianStream {
    Stream stream;
    public BigEndianStream(Stream stream) {
        this.stream = stream;
    }

    public bool HasRemaining() {
        return (stream.Length - stream.Position) > 0;
    }

    public long Position {
        get => stream.Position;
        set => stream.Position = value;
    }

    public void WriteBytes(byte[] value) {
        stream.Write(value, 0, value.Length);
    }

    public void WriteByte(byte value) {
        stream.WriteByte(value);
    }

    public void WriteUShort(ushort value) {
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    public void WriteUInt(uint value) {
        stream.WriteByte((byte)(value >> 24));
        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    public void WriteULong(ulong value) {
        WriteUInt((uint)(value >> 32));
        WriteUInt((uint)value);
    }

    public void WriteSByte(sbyte value) {
        stream.WriteByte((byte)value);
    }

    public void WriteShort(short value) {
        WriteUShort((ushort)value);
    }

    public void WriteInt(int value) {
        WriteUInt((uint)value);
    }

    public void WriteFloat(float value) {
        WriteInt(BitConverter.SingleToInt32Bits(value));
    }

    public void WriteLong(long value) {
        WriteULong((ulong)value);
    }

    public void WriteDouble(double value) {
        WriteLong(BitConverter.DoubleToInt64Bits(value));
    }

    public byte[] ReadBytes(int length) {
        CheckRemaining(length);
        byte[] result = new byte[length];
        stream.Read(result, 0, length);
        return result;
    }

    public byte ReadByte() {
        CheckRemaining(1);
        return Convert.ToByte(stream.ReadByte());
    }

    public ushort ReadUShort() {
        CheckRemaining(2);
        return (ushort)(stream.ReadByte() << 8 | stream.ReadByte());
    }

    public uint ReadUInt() {
        CheckRemaining(4);
        return (uint)(stream.ReadByte() << 24
            | stream.ReadByte() << 16
            | stream.ReadByte() << 8
            | stream.ReadByte());
    }

    public ulong ReadULong() {
        return (ulong)ReadUInt() << 32 | (ulong)ReadUInt();
    }

    public sbyte ReadSByte() {
        return (sbyte)ReadByte();
    }

    public short ReadShort() {
        return (short)ReadUShort();
    }

    public int ReadInt() {
        return (int)ReadUInt();
    }

    public float ReadFloat() {
        return BitConverter.Int32BitsToSingle(ReadInt());
    }

    public long ReadLong() {
        return (long)ReadULong();
    }

    public double ReadDouble() {
        return BitConverter.Int64BitsToDouble(ReadLong());
    }

    private void CheckRemaining(int length) {
        if (stream.Length - stream.Position < length) {
            throw new StreamUnderflowException();
        }
    }
}

// Contains loading, initialization code,
// and the FFI Function declarations in a com.sun.jna.Library.


// This is an implementation detail which will be called internally by the public API.
static class _UniFFILib {
    static _UniFFILib() {
        _UniFFILib.uniffiCheckContractApiVersion();
        _UniFFILib.uniffiCheckApiChecksums();
        
        }

    [DllImport("rust_lib")]
    public static extern void uniffi_rust_lib_fn_func_fetch_inbox_top(RustBuffer @domain,ushort @port,RustBuffer @username,RustBuffer @password,ref RustCallStatus _uniffi_out_err
    );

    [DllImport("rust_lib")]
    public static extern void uniffi_rust_lib_fn_func_send_plain_text_email(RustBuffer @from,RustBuffer @replyTo,RustBuffer @to,RustBuffer @subject,RustBuffer @body,RustBuffer @smtpServer,RustBuffer @smtpUsername,RustBuffer @smtpPassword,ref RustCallStatus _uniffi_out_err
    );

    [DllImport("rust_lib")]
    public static extern RustBuffer ffi_rust_lib_rustbuffer_alloc(int @size,ref RustCallStatus _uniffi_out_err
    );

    [DllImport("rust_lib")]
    public static extern RustBuffer ffi_rust_lib_rustbuffer_from_bytes(ForeignBytes @bytes,ref RustCallStatus _uniffi_out_err
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rustbuffer_free(RustBuffer @buf,ref RustCallStatus _uniffi_out_err
    );

    [DllImport("rust_lib")]
    public static extern RustBuffer ffi_rust_lib_rustbuffer_reserve(RustBuffer @buf,int @additional,ref RustCallStatus _uniffi_out_err
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_continuation_callback_set(IntPtr @callback
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_poll_u8(IntPtr @handle,IntPtr @uniffiCallback
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_cancel_u8(IntPtr @handle
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_free_u8(IntPtr @handle
    );

    [DllImport("rust_lib")]
    public static extern byte ffi_rust_lib_rust_future_complete_u8(IntPtr @handle,ref RustCallStatus _uniffi_out_err
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_poll_i8(IntPtr @handle,IntPtr @uniffiCallback
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_cancel_i8(IntPtr @handle
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_free_i8(IntPtr @handle
    );

    [DllImport("rust_lib")]
    public static extern sbyte ffi_rust_lib_rust_future_complete_i8(IntPtr @handle,ref RustCallStatus _uniffi_out_err
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_poll_u16(IntPtr @handle,IntPtr @uniffiCallback
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_cancel_u16(IntPtr @handle
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_free_u16(IntPtr @handle
    );

    [DllImport("rust_lib")]
    public static extern ushort ffi_rust_lib_rust_future_complete_u16(IntPtr @handle,ref RustCallStatus _uniffi_out_err
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_poll_i16(IntPtr @handle,IntPtr @uniffiCallback
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_cancel_i16(IntPtr @handle
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_free_i16(IntPtr @handle
    );

    [DllImport("rust_lib")]
    public static extern short ffi_rust_lib_rust_future_complete_i16(IntPtr @handle,ref RustCallStatus _uniffi_out_err
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_poll_u32(IntPtr @handle,IntPtr @uniffiCallback
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_cancel_u32(IntPtr @handle
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_free_u32(IntPtr @handle
    );

    [DllImport("rust_lib")]
    public static extern uint ffi_rust_lib_rust_future_complete_u32(IntPtr @handle,ref RustCallStatus _uniffi_out_err
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_poll_i32(IntPtr @handle,IntPtr @uniffiCallback
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_cancel_i32(IntPtr @handle
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_free_i32(IntPtr @handle
    );

    [DllImport("rust_lib")]
    public static extern int ffi_rust_lib_rust_future_complete_i32(IntPtr @handle,ref RustCallStatus _uniffi_out_err
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_poll_u64(IntPtr @handle,IntPtr @uniffiCallback
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_cancel_u64(IntPtr @handle
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_free_u64(IntPtr @handle
    );

    [DllImport("rust_lib")]
    public static extern ulong ffi_rust_lib_rust_future_complete_u64(IntPtr @handle,ref RustCallStatus _uniffi_out_err
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_poll_i64(IntPtr @handle,IntPtr @uniffiCallback
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_cancel_i64(IntPtr @handle
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_free_i64(IntPtr @handle
    );

    [DllImport("rust_lib")]
    public static extern long ffi_rust_lib_rust_future_complete_i64(IntPtr @handle,ref RustCallStatus _uniffi_out_err
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_poll_f32(IntPtr @handle,IntPtr @uniffiCallback
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_cancel_f32(IntPtr @handle
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_free_f32(IntPtr @handle
    );

    [DllImport("rust_lib")]
    public static extern float ffi_rust_lib_rust_future_complete_f32(IntPtr @handle,ref RustCallStatus _uniffi_out_err
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_poll_f64(IntPtr @handle,IntPtr @uniffiCallback
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_cancel_f64(IntPtr @handle
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_free_f64(IntPtr @handle
    );

    [DllImport("rust_lib")]
    public static extern double ffi_rust_lib_rust_future_complete_f64(IntPtr @handle,ref RustCallStatus _uniffi_out_err
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_poll_pointer(IntPtr @handle,IntPtr @uniffiCallback
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_cancel_pointer(IntPtr @handle
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_free_pointer(IntPtr @handle
    );

    [DllImport("rust_lib")]
    public static extern SafeHandle ffi_rust_lib_rust_future_complete_pointer(IntPtr @handle,ref RustCallStatus _uniffi_out_err
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_poll_rust_buffer(IntPtr @handle,IntPtr @uniffiCallback
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_cancel_rust_buffer(IntPtr @handle
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_free_rust_buffer(IntPtr @handle
    );

    [DllImport("rust_lib")]
    public static extern RustBuffer ffi_rust_lib_rust_future_complete_rust_buffer(IntPtr @handle,ref RustCallStatus _uniffi_out_err
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_poll_void(IntPtr @handle,IntPtr @uniffiCallback
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_cancel_void(IntPtr @handle
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_free_void(IntPtr @handle
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_complete_void(IntPtr @handle,ref RustCallStatus _uniffi_out_err
    );

    [DllImport("rust_lib")]
    public static extern ushort uniffi_rust_lib_checksum_func_fetch_inbox_top(
    );

    [DllImport("rust_lib")]
    public static extern ushort uniffi_rust_lib_checksum_func_send_plain_text_email(
    );

    [DllImport("rust_lib")]
    public static extern uint ffi_rust_lib_uniffi_contract_version(
    );

    

    static void uniffiCheckContractApiVersion() {
        var scaffolding_contract_version = _UniFFILib.ffi_rust_lib_uniffi_contract_version();
        if (24 != scaffolding_contract_version) {
            throw new UniffiContractVersionException($"uniffi.rust_lib: uniffi bindings expected version `24`, library returned `{scaffolding_contract_version}`");
        }
    }

    static void uniffiCheckApiChecksums() {
        {
            var checksum = _UniFFILib.uniffi_rust_lib_checksum_func_fetch_inbox_top();
            if (checksum != 48774) {
                throw new UniffiContractChecksumException($"uniffi.rust_lib: uniffi bindings expected function `uniffi_rust_lib_checksum_func_fetch_inbox_top` checksum `48774`, library returned `{checksum}`");
            }
        }
        {
            var checksum = _UniFFILib.uniffi_rust_lib_checksum_func_send_plain_text_email();
            if (checksum != 25723) {
                throw new UniffiContractChecksumException($"uniffi.rust_lib: uniffi bindings expected function `uniffi_rust_lib_checksum_func_send_plain_text_email` checksum `25723`, library returned `{checksum}`");
            }
        }
    }
}

// Public interface members begin here.

#pragma warning disable 8625




class FfiConverterUInt16: FfiConverter<ushort, ushort> {
    public static FfiConverterUInt16 INSTANCE = new FfiConverterUInt16();

    public override ushort Lift(ushort value) {
        return value;
    }

    public override ushort Read(BigEndianStream stream) {
        return stream.ReadUShort();
    }

    public override ushort Lower(ushort value) {
        return value;
    }

    public override int AllocationSize(ushort value) {
        return 2;
    }

    public override void Write(ushort value, BigEndianStream stream) {
        stream.WriteUShort(value);
    }
}



class FfiConverterString: FfiConverter<string, RustBuffer> {
    public static FfiConverterString INSTANCE = new FfiConverterString();

    // Note: we don't inherit from FfiConverterRustBuffer, because we use a
    // special encoding when lowering/lifting.  We can use `RustBuffer.len` to
    // store our length and avoid writing it out to the buffer.
    public override string Lift(RustBuffer value) {
        try {
            var bytes = value.AsStream().ReadBytes(value.len);
            return System.Text.Encoding.UTF8.GetString(bytes);
        } finally {
            RustBuffer.Free(value);
        }
    }

    public override string Read(BigEndianStream stream) {
        var length = stream.ReadInt();
        var bytes = stream.ReadBytes(length);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    public override RustBuffer Lower(string value) {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        var rbuf = RustBuffer.Alloc(bytes.Length);
        rbuf.AsWriteableStream().WriteBytes(bytes);
        return rbuf;
    }

    // TODO(CS)
    // We aren't sure exactly how many bytes our string will be once it's UTF-8
    // encoded.  Allocate 3 bytes per unicode codepoint which will always be
    // enough.
    public override int AllocationSize(string value) {
        const int sizeForLength = 4;
        var sizeForString = value.Length * 3;
        return sizeForLength + sizeForString;
    }

    public override void Write(string value, BigEndianStream stream) {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        stream.WriteInt(bytes.Length);
        stream.WriteBytes(bytes);
    }
}
#pragma warning restore 8625
public static class RustLibMethods {
    public static void FetchInboxTop(String @domain, ushort @port, String @username, String @password) {
        
    _UniffiHelpers.RustCall( (ref RustCallStatus _status) =>
    _UniFFILib.uniffi_rust_lib_fn_func_fetch_inbox_top(FfiConverterString.INSTANCE.Lower(@domain), FfiConverterUInt16.INSTANCE.Lower(@port), FfiConverterString.INSTANCE.Lower(@username), FfiConverterString.INSTANCE.Lower(@password), ref _status)
);
    }

    public static void SendPlainTextEmail(String @from, String @replyTo, String @to, String @subject, String @body, String @smtpServer, String @smtpUsername, String @smtpPassword) {
        
    _UniffiHelpers.RustCall( (ref RustCallStatus _status) =>
    _UniFFILib.uniffi_rust_lib_fn_func_send_plain_text_email(FfiConverterString.INSTANCE.Lower(@from), FfiConverterString.INSTANCE.Lower(@replyTo), FfiConverterString.INSTANCE.Lower(@to), FfiConverterString.INSTANCE.Lower(@subject), FfiConverterString.INSTANCE.Lower(@body), FfiConverterString.INSTANCE.Lower(@smtpServer), FfiConverterString.INSTANCE.Lower(@smtpUsername), FfiConverterString.INSTANCE.Lower(@smtpPassword), ref _status)
);
    }

}

