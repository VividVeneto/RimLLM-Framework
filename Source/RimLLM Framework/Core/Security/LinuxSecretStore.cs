using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Threading;

namespace RimLLM_Framework.Core;

/// <summary>透過 libsecret 使用登入工作階段的 Secret Service，不將秘密放進程序參數或檔案。</summary>
[SupportedOSPlatform("linux")]
internal sealed class LinuxSecretStore : ISecretStore
{
    private const string Secret = "libsecret-1.so.0";
    private const string Glib = "libglib-2.0.so.0";
    private const string Gio = "libgio-2.0.so.0";
    private const string GObject = "libgobject-2.0.so.0";

    public byte[] Read(string account)
    {
        using var call = new SecretCall(account);
        IntPtr value = secret_password_lookupv_sync(IntPtr.Zero, call.Attributes, call.Cancellable, out IntPtr error);
        try
        {
            CheckError(error);
            return value == IntPtr.Zero ? null : Convert.FromBase64String(Marshal.PtrToStringUTF8(value));
        }
        finally { if (value != IntPtr.Zero) secret_password_free(value); }
    }

    public void Write(string account, byte[] key)
    {
        using var call = new SecretCall(account);
        int success = secret_password_storev_sync(IntPtr.Zero, call.Attributes, null, "RimLLM Framework storage key",
            Convert.ToBase64String(key), call.Cancellable, out IntPtr error);
        CheckError(error);
        if (success == 0) throw new CryptographicException("Secret Service did not store the storage key.");
    }

    public void Delete(string account)
    {
        using var call = new SecretCall(account);
        secret_password_clearv_sync(IntPtr.Zero, call.Attributes, call.Cancellable, out IntPtr error);
        CheckError(error);
    }

    private static void CheckError(IntPtr error)
    {
        if (error == IntPtr.Zero) return;
        try
        {
            // 原生訊息不帶進日誌，避免服務提供者將敏感內容寫入錯誤文字。
            int code = Marshal.ReadInt32(error, sizeof(uint));
            throw new CryptographicException($"Secret Service failed (code {code}). Ensure the session D-Bus and an unlocked keyring are available.");
        }
        finally { g_error_free(error); }
    }

    private sealed class SecretCall : IDisposable
    {
        private IntPtr _glib;
        private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(10));
        private CancellationTokenRegistration _registration;
        public IntPtr Attributes { get; private set; }
        public IntPtr Cancellable { get; private set; }

        public SecretCall(string account)
        {
            try
            {
                _glib = NativeLibrary.Load(Glib);
                Attributes = g_hash_table_new_full(NativeLibrary.GetExport(_glib, "g_str_hash"), NativeLibrary.GetExport(_glib, "g_str_equal"),
                    NativeLibrary.GetExport(_glib, "g_free"), NativeLibrary.GetExport(_glib, "g_free"));
                if (Attributes == IntPtr.Zero) throw new CryptographicException("Could not allocate Secret Service attributes.");
                g_hash_table_insert(Attributes, g_strdup("application"), g_strdup("GreenMushroom.RimLLMFramework"));
                g_hash_table_insert(Attributes, g_strdup("account"), g_strdup(account));
                Cancellable = g_cancellable_new();
                if (Cancellable == IntPtr.Zero) throw new CryptographicException("Could not allocate Secret Service cancellation.");
                _registration = _timeout.Token.Register(() => g_cancellable_cancel(Cancellable));
            }
            catch (Exception ex)
            {
                Dispose();
                throw new CryptographicException("Linux secure storage requires libsecret-1 and a session Secret Service.", ex);
            }
        }

        public void Dispose()
        {
            // 先等待取消回呼結束，才釋放它可能還在使用的原生物件。
            _registration.Dispose();
            _timeout.Dispose();
            if (Cancellable != IntPtr.Zero) { g_object_unref(Cancellable); Cancellable = IntPtr.Zero; }
            if (Attributes != IntPtr.Zero) { g_hash_table_unref(Attributes); Attributes = IntPtr.Zero; }
            if (_glib != IntPtr.Zero) { NativeLibrary.Free(_glib); _glib = IntPtr.Zero; }
        }
    }

    [DllImport(Secret, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr secret_password_lookupv_sync(IntPtr schema, IntPtr attributes, IntPtr cancellable, out IntPtr error);
    [DllImport(Secret, CallingConvention = CallingConvention.Cdecl)]
    private static extern int secret_password_storev_sync(IntPtr schema, IntPtr attributes,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string collection, [MarshalAs(UnmanagedType.LPUTF8Str)] string label,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string password, IntPtr cancellable, out IntPtr error);
    [DllImport(Secret, CallingConvention = CallingConvention.Cdecl)]
    private static extern int secret_password_clearv_sync(IntPtr schema, IntPtr attributes, IntPtr cancellable, out IntPtr error);
    [DllImport(Secret, CallingConvention = CallingConvention.Cdecl)]
    private static extern void secret_password_free(IntPtr password);
    [DllImport(Glib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr g_hash_table_new_full(IntPtr hash, IntPtr equal, IntPtr freeKey, IntPtr freeValue);
    [DllImport(Glib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int g_hash_table_insert(IntPtr table, IntPtr key, IntPtr value);
    [DllImport(Glib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void g_hash_table_unref(IntPtr table);
    [DllImport(Glib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr g_strdup([MarshalAs(UnmanagedType.LPUTF8Str)] string text);
    [DllImport(Glib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void g_error_free(IntPtr error);
    [DllImport(Gio, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr g_cancellable_new();
    [DllImport(Gio, CallingConvention = CallingConvention.Cdecl)]
    private static extern void g_cancellable_cancel(IntPtr cancellable);
    [DllImport(GObject, CallingConvention = CallingConvention.Cdecl)]
    private static extern void g_object_unref(IntPtr instance);
}
