package com.example.dingtalkpunch

import android.content.Context

data class PunchTime(val hour: Int, val minute: Int)

object ScheduleStorage {
    private const val PREFS = "dingtalk_punch_prefs"
    private const val KEY_TIMES = "punch_times"
    private const val KEY_AUTO = "auto_click"

    fun load(context: Context): MutableList<PunchTime> {
        val prefs = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
        val raw = prefs.getString(KEY_TIMES, "") ?: ""
        if (raw.isBlank()) return mutableListOf()
        return raw.split(";").mapNotNull { part ->
            val segs = part.split(":")
            val h = segs.getOrNull(0)?.toIntOrNull()
            val m = segs.getOrNull(1)?.toIntOrNull()
            if (h != null && m != null && h in 0..23 && m in 0..59) PunchTime(h, m) else null
        }.toMutableList()
    }

    fun save(context: Context, list: List<PunchTime>) {
        val raw = list.joinToString(";") { "${it.hour}:${it.minute}" }
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
            .edit().putString(KEY_TIMES, raw).apply()
    }

    fun loadAutoClick(context: Context): Boolean =
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getBoolean(KEY_AUTO, false)

    fun saveAutoClick(context: Context, v: Boolean) {
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().putBoolean(KEY_AUTO, v).apply()
    }
}
