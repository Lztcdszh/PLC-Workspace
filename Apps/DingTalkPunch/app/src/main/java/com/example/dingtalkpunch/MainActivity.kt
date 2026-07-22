package com.example.dingtalkpunch

import android.Manifest
import android.app.TimePickerDialog
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.provider.Settings
import android.text.format.DateFormat
import android.view.Gravity
import android.widget.*
import androidx.appcompat.app.AppCompatActivity
import androidx.core.app.ActivityCompat
import androidx.core.content.ContextCompat
import java.util.Calendar

class MainActivity : AppCompatActivity() {

    private val times = mutableListOf<PunchTime>()
    private val REQ_POST_NOTIF = 100

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)

        times.addAll(ScheduleStorage.load(this))
        val autoClick = findViewById<Switch>(R.id.autoClick)
        autoClick.isChecked = ScheduleStorage.loadAutoClick(this)
        renderTimes()

        findViewById<Button>(R.id.addTime).setOnClickListener { showTimePicker() }

        autoClick.setOnCheckedChangeListener { _, isChecked ->
            ScheduleStorage.saveAutoClick(this, isChecked)
            if (isChecked) openAccessibilitySettings()
        }

        findViewById<Button>(R.id.btnAccessibility).setOnClickListener { openAccessibilitySettings() }
        findViewById<Button>(R.id.btnExactAlarm).setOnClickListener { openExactAlarmSettings() }
        findViewById<Button>(R.id.btnBattery).setOnClickListener { openBatterySettings() }
        findViewById<Button>(R.id.btnXiaomi).setOnClickListener { openXiaomiSettings() }
        findViewById<Button>(R.id.btnSave).setOnClickListener {
            ScheduleStorage.save(this, times)
            AlarmScheduler.scheduleAll(this)
            updateStatus()
            Toast.makeText(this, R.string.saved, Toast.LENGTH_SHORT).show()
        }

        // Android 13+ 申请通知权限
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            if (ContextCompat.checkSelfPermission(this, Manifest.permission.POST_NOTIFICATIONS)
                != PackageManager.PERMISSION_GRANTED
            ) {
                ActivityCompat.requestPermissions(
                    this,
                    arrayOf(Manifest.permission.POST_NOTIFICATIONS),
                    REQ_POST_NOTIF
                )
            }
        }

        if (!DingTalkHelper.isInstalled(this)) {
            Toast.makeText(this, R.string.dingtalk_not_installed, Toast.LENGTH_LONG).show()
        }

        updateStatus()
    }

    private fun renderTimes() {
        val container = findViewById<LinearLayout>(R.id.listTimes)
        container.removeAllViews()
        if (times.isEmpty()) {
            val tv = TextView(this)
            tv.text = getString(R.string.no_times)
            container.addView(tv)
            return
        }
        times.forEachIndexed { idx, t ->
            val row = LinearLayout(this).apply {
                orientation = LinearLayout.HORIZONTAL
                gravity = Gravity.CENTER_VERTICAL
                setPadding(0, 8, 0, 8)
            }
            val tv = TextView(this).apply {
                text = String.format("%02d:%02d", t.hour, t.minute)
                textSize = 18f
                layoutParams = LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f)
            }
            val del = Button(this).apply {
                text = getString(R.string.delete)
                setOnClickListener {
                    times.removeAt(idx)
                    ScheduleStorage.save(this@MainActivity, times)
                    renderTimes()
                    updateStatus()
                }
            }
            row.addView(tv)
            row.addView(del)
            container.addView(row)
        }
    }

    private fun showTimePicker() {
        val cal = Calendar.getInstance()
        TimePickerDialog(
            this,
            { _: TimePicker, h: Int, m: Int ->
                times.add(PunchTime(h, m))
                ScheduleStorage.save(this, times)
                renderTimes()
                updateStatus()
            },
            cal.get(Calendar.HOUR_OF_DAY),
            cal.get(Calendar.MINUTE),
            true
        ).show()
    }

    private fun openExactAlarmSettings() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            try {
                startActivity(
                    Intent(
                        Settings.ACTION_REQUEST_SCHEDULE_EXACT_ALARM,
                        Uri.parse("package:$packageName")
                    )
                )
            } catch (e: Exception) {
                Toast.makeText(this, "请手动在系统设置中授予“精确闹钟”权限", Toast.LENGTH_LONG).show()
            }
        } else {
            Toast.makeText(this, "当前系统版本无需额外授权", Toast.LENGTH_SHORT).show()
        }
    }

    private fun openBatterySettings() {
        try {
            startActivity(
                Intent(
                    Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS,
                    Uri.parse("package:$packageName")
                )
            )
        } catch (e: Exception) {
            try {
                startActivity(
                    Intent(
                        Settings.ACTION_APPLICATION_DETAILS_SETTINGS,
                        Uri.parse("package:$packageName")
                    )
                )
            } catch (e2: Exception) {
                // 忽略
            }
        }
    }

    private fun openAccessibilitySettings() {
        startActivity(Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS))
    }

    private fun openXiaomiSettings() {
        val candidates = listOf(
            Intent().setClassName(
                "com.miui.securitycenter",
                "com.miui.permcenter.autosettings.AutoSettingsActivity"
            ),
            Intent("miui.intent.action.APP_PERM_EDITOR")
                .setClassName(
                    "com.miui.securitycenter",
                    "com.miui.permcenter.permissions.PermissionsEditorActivity"
                )
                .putExtra("extra_pkgname", packageName),
            Intent(
                Settings.ACTION_APPLICATION_DETAILS_SETTINGS,
                Uri.parse("package:$packageName")
            )
        )
        for (intent in candidates) {
            try {
                startActivity(intent)
                return
            } catch (e: Exception) {
                // 尝试下一个
            }
        }
    }

    private fun updateStatus() {
        val status = findViewById<TextView>(R.id.status)
        val list = ScheduleStorage.load(this)
        if (list.isEmpty()) {
            status.text = getString(R.string.no_times)
            return
        }
        val now = Calendar.getInstance()
        var next: Calendar? = null
        for (t in list) {
            val c = Calendar.getInstance().apply {
                set(Calendar.HOUR_OF_DAY, t.hour)
                set(Calendar.MINUTE, t.minute)
                set(Calendar.SECOND, 0)
                set(Calendar.MILLISECOND, 0)
            }
            if (c.timeInMillis <= now.timeInMillis) c.add(Calendar.DATE, 1)
            if (next == null || c.timeInMillis < next.timeInMillis) next = c
        }
        val fmt = DateFormat.getTimeFormat(this)
        status.text = getString(R.string.next_alarm, fmt.format(next!!.time))
    }
}
