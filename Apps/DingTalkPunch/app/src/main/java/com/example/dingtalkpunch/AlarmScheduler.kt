package com.example.dingtalkpunch

import android.app.AlarmManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.os.Build
import java.util.Calendar

object AlarmScheduler {
    private const val REQUEST_BASE = 1000

    /** 是否可设置精确闹钟（Android 12+ 需系统授权，其余版本恒 true） */
    fun canScheduleExact(context: Context): Boolean {
        val am = context.getSystemService(Context.ALARM_SERVICE) as AlarmManager
        return if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            am.canScheduleExactAlarms()
        } else {
            true
        }
    }

    /**
     * 排程所有打卡时间。返回 true 表示已用精确闹钟，false 表示因无权限降级为非精确闹钟。
     * 内部对 SecurityException 做兜底，保证任何情况下都不会崩溃。
     */
    fun scheduleAll(context: Context): Boolean {
        cancelAll(context)
        val times = ScheduleStorage.load(context)
        val am = context.getSystemService(Context.ALARM_SERVICE) as AlarmManager
        val exact = canScheduleExact(context)
        times.forEachIndexed { index, t ->
            val intent = Intent(context, AlarmReceiver::class.java)
            val pi = PendingIntent.getBroadcast(
                context, REQUEST_BASE + index,
                intent, PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
            )
            val cal = Calendar.getInstance().apply {
                set(Calendar.HOUR_OF_DAY, t.hour)
                set(Calendar.MINUTE, t.minute)
                set(Calendar.SECOND, 0)
                set(Calendar.MILLISECOND, 0)
            }
            // 若今天该时间已过，顺延到明天
            if (cal.timeInMillis <= System.currentTimeMillis()) {
                cal.add(Calendar.DATE, 1)
            }
            try {
                when {
                    Build.VERSION.SDK_INT >= Build.VERSION_CODES.M && exact ->
                        am.setExactAndAllowWhileIdle(AlarmManager.RTC_WAKEUP, cal.timeInMillis, pi)
                    Build.VERSION.SDK_INT >= Build.VERSION_CODES.M ->
                        // 无精确闹钟权限：降级为非精确（可能有几分钟偏差，但不会崩溃/不需授权）
                        am.setAndAllowWhileIdle(AlarmManager.RTC_WAKEUP, cal.timeInMillis, pi)
                    else ->
                        am.setExact(AlarmManager.RTC_WAKEUP, cal.timeInMillis, pi)
                }
            } catch (e: SecurityException) {
                // 极端兜底：任何精确/allow-while-idle 失败时，退回最普通的 set
                try {
                    am.set(AlarmManager.RTC_WAKEUP, cal.timeInMillis, pi)
                } catch (ignored: Exception) {
                }
            }
        }
        return exact
    }

    fun cancelAll(context: Context) {
        val am = context.getSystemService(Context.ALARM_SERVICE) as AlarmManager
        val count = ScheduleStorage.load(context).size
        for (i in 0 until count) {
            val intent = Intent(context, AlarmReceiver::class.java)
            val pi = PendingIntent.getBroadcast(
                context, REQUEST_BASE + i,
                intent, PendingIntent.FLAG_NO_CREATE or PendingIntent.FLAG_IMMUTABLE
            )
            pi?.let { am.cancel(it) }
        }
    }
}
