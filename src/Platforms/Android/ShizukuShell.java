package com.agate.bridge;

import android.os.IBinder;
import android.os.ParcelFileDescriptor;

import java.io.ByteArrayOutputStream;
import java.io.FileInputStream;
import java.io.InputStream;

import moe.shizuku.server.IRemoteProcess;
import moe.shizuku.server.IShizukuService;
import rikka.shizuku.Shizuku;
import rikka.shizuku.ShizukuBinderWrapper;

/**
 * C# 侧通过 JNI 调用的静态门面。
 *
 * Android 11+ 对 Android/data、Android/obb 做了系统级封锁，普通应用即使拿到
 * 「所有文件访问」也进不去。Shizuku 通过 adb/root 启动的常驻服务提供 shell 身份，
 * 这里用 IShizukuService.newProcess 起子进程执行命令，绕开该限制。
 *
 * 所有方法都返回字符串或整数，不抛异常到 C#，避免 JNI 层崩溃。
 */
public final class ShizukuShell {

    public static final int STATE_UNAVAILABLE = 0;   // 没装 Shizuku，或服务没启动
    public static final int STATE_NO_PERMISSION = 1; // 装了但本应用未授权
    public static final int STATE_READY = 2;         // 可用

    private static volatile int lastPermissionResult = Integer.MIN_VALUE;
    private static volatile boolean listenerAdded = false;

    private ShizukuShell() {
    }

    /** 当前可用状态，见 STATE_*。 */
    public static int state() {
        try {
            if (!Shizuku.pingBinder()) return STATE_UNAVAILABLE;
            if (Shizuku.isPreV11()) return STATE_NO_PERMISSION;
            return Shizuku.checkSelfPermission() == 0 ? STATE_READY : STATE_NO_PERMISSION;
        } catch (Throwable t) {
            return STATE_UNAVAILABLE;
        }
    }

    /** Shizuku 服务版本号，未连接时为 -1。 */
    public static int version() {
        try {
            return Shizuku.getVersion();
        } catch (Throwable t) {
            return -1;
        }
    }

    /** 执行命令的 uid（通常 2000 即 shell），不可用时 -1。 */
    public static int shellUid() {
        try {
            if (!Shizuku.pingBinder()) return -1;
            return Shizuku.getUid();
        } catch (Throwable t) {
            return -1;
        }
    }

    /** 发起授权请求；结果异步写进 lastPermissionResult。 */
    public static void requestPermission(int requestCode) {
        try {
            ensureListener();
            Shizuku.requestPermission(requestCode);
        } catch (Throwable t) {
            lastPermissionResult = -1;
        }
    }

    /** 最近一次授权结果：0=允许，-1=拒绝，Integer.MIN_VALUE=还没结果。 */
    public static int lastPermissionResult() {
        return lastPermissionResult;
    }

    public static void resetPermissionResult() {
        lastPermissionResult = Integer.MIN_VALUE;
    }

    private static synchronized void ensureListener() {
        if (listenerAdded) return;
        listenerAdded = true;
        Shizuku.addRequestPermissionResultListener(new Shizuku.OnRequestPermissionResultListener() {
            @Override
            public void onRequestPermissionResult(int requestCode, int grantResult) {
                lastPermissionResult = grantResult;
            }
        });
    }

    private static IShizukuService service() throws Exception {
        IBinder binder = Shizuku.getBinder();
        if (binder == null) throw new IllegalStateException("Shizuku binder is null");
        return IShizukuService.Stub.asInterface(new ShizukuBinderWrapper(binder));
    }

    /**
     * 执行一条 sh 命令，返回 "退出码\n标准输出"。
     * 退出码为 -1 表示根本没跑起来，第二行是异常描述。
     */
    public static String exec(String command) {
        IRemoteProcess process = null;
        try {
            IShizukuService svc = service();
            process = svc.newProcess(new String[]{"sh", "-c", command}, null, "/");
            byte[] out = readAll(process.getInputStream());
            int code = process.waitFor();
            return code + "\n" + new String(out, "UTF-8");
        } catch (Throwable t) {
            return "-1\n" + t.getClass().getSimpleName() + ": " + t.getMessage();
        } finally {
            if (process != null) {
                try {
                    process.destroy();
                } catch (Throwable ignored) {
                }
            }
        }
    }

    /**
     * 列目录，每行 "大小|类型|绝对路径"。
     * 用 stat 而不是 ls -l，避免文件名含空格时列错位。
     */
    public static String list(String dir) {
        String q = quote(dir);
        return exec("find " + q + " -mindepth 1 -maxdepth 1 -exec stat -c '%s|%F|%n' {} +");
    }

    /** 把受限目录里的文件复制到本应用可读的位置。 */
    public static String copy(String src, String dst) {
        return exec("cp -f " + quote(src) + " " + quote(dst));
    }

    public static String remove(String path, boolean recursive) {
        return exec("rm " + (recursive ? "-rf " : "-f ") + quote(path));
    }

    public static String rename(String src, String dst) {
        return exec("mv -f " + quote(src) + " " + quote(dst));
    }

    /** 单引号包裹，内部单引号用 '\'' 断开再拼接，防止命令注入与空格截断。 */
    private static String quote(String value) {
        if (value == null) return "''";
        return "'" + value.replace("'", "'\\''") + "'";
    }

    private static byte[] readAll(ParcelFileDescriptor pfd) throws Exception {
        if (pfd == null) return new byte[0];
        ByteArrayOutputStream buffer = new ByteArrayOutputStream();
        InputStream input = new FileInputStream(pfd.getFileDescriptor());
        try {
            byte[] chunk = new byte[8192];
            int read;
            while ((read = input.read(chunk)) > 0) {
                buffer.write(chunk, 0, read);
            }
        } finally {
            try {
                input.close();
            } catch (Throwable ignored) {
            }
            try {
                pfd.close();
            } catch (Throwable ignored) {
            }
        }
        return buffer.toByteArray();
    }
}
