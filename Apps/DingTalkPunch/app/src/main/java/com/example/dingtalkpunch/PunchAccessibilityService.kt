package com.example.dingtalkpunch

import android.accessibilityservice.AccessibilityService
import android.view.accessibility.AccessibilityEvent
import android.view.accessibility.AccessibilityNodeInfo

class PunchAccessibilityService : AccessibilityService() {

    private var lastClick = 0L

    override fun onAccessibilityEvent(event: AccessibilityEvent) {
        val pkg = event.packageName?.toString() ?: return
        if (pkg != DingTalkHelper.PACKAGE) return
        if (!ScheduleStorage.loadAutoClick(this)) return

        // 防抖：8 秒内只点一次，避免重复触发
        val now = System.currentTimeMillis()
        if (now - lastClick < 8000) return

        val root = rootInActiveWindow ?: return
        findAndClick(root)
    }

    private fun findAndClick(node: AccessibilityNodeInfo?) {
        if (node == null) return
        val text = node.text?.toString() ?: ""
        val desc = node.contentDescription?.toString() ?: ""
        val combined = "$text $desc"
        val targets = listOf("上班打卡", "下班打卡", "打卡")
        if (node.isClickable && targets.any { combined.contains(it) }) {
            node.performAction(AccessibilityNodeInfo.ACTION_CLICK)
            lastClick = System.currentTimeMillis()
            return
        }
        for (i in 0 until node.childCount) {
            findAndClick(node.getChild(i))
        }
    }

    override fun onInterrupt() {}
}
