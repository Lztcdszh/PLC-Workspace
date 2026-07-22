package com.example.dingtalkpunch

import android.app.Activity
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.WindowManager

class LaunchProxyActivity : Activity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        // 保持亮屏，确保钉钉弹出后可见
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        DingTalkHelper.launch(this)
        // 短暂驻留后关闭透明中转页
        Handler(Looper.getMainLooper()).postDelayed({ finish() }, 2000)
    }
}
