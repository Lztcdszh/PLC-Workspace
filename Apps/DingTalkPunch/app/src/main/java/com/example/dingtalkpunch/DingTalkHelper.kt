package com.example.dingtalkpunch

import android.content.Context
import android.content.Intent

object DingTalkHelper {
    const val PACKAGE = "com.alibaba.android.rimet"

    fun isInstalled(context: Context): Boolean =
        try {
            context.packageManager.getPackageInfo(PACKAGE, 0)
            true
        } catch (e: Exception) {
            // 兜底：某些 ROM 上 getPackageInfo 可能抛异常，改用启动 Intent 再判断一次
            context.packageManager.getLaunchIntentForPackage(PACKAGE) != null
        }

    fun launch(context: Context) {
        val pm = context.packageManager
        val intent = pm.getLaunchIntentForPackage(PACKAGE)
        if (intent != null) {
            intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP)
            context.startActivity(intent)
        } else {
            // 未安装钉钉时跳转到应用市场
            val market = Intent(Intent.ACTION_VIEW, android.net.Uri.parse("market://details?id=$PACKAGE"))
            market.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
            context.startActivity(market)
        }
    }
}
