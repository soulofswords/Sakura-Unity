using System;
using System.Reflection;
using UnityEngine;

namespace Sakura.Unity.Editor
{
    public static class SakuraAvatarTouchChecks
    {
        public static void Run()
        {
            const BindingFlags InternalStatic = BindingFlags.NonPublic | BindingFlags.Static;
            var viewport = typeof(SakuraAvatarViewport);
            var tap = viewport.GetMethod("IsTouchTap", InternalStatic);
            var head = viewport.GetMethod("IsHeadTouch", InternalStatic);
            Require(tap != null && head != null, "The viewport has no avatar touch classification.");

            bool Tap(float x, float y, float elapsed) =>
                (bool)tap.Invoke(null, new object[] { new Vector2(x, y), elapsed });
            bool Head(float point, float bottom, float top) =>
                (bool)head.Invoke(null, new object[] { point, bottom, top });

            Require(Tap(2, 3, .12f), "A short tap with minor pointer movement should react.");
            Require(!Tap(14, 0, .12f), "A positioning drag should not react.");
            Require(!Tap(0, 0, .8f), "A long press should not react.");
            Require(Head(.8f, .2f, .9f), "A tap near the top of the model should be a headpat.");
            Require(!Head(.5f, .2f, .9f), "A body tap should be a poke.");
            Require(Head(.55f, -.4f, .65f), "Head detection should follow zoom and framing.");
            Require(!Head(.55f, .5f, .5f), "An empty projected model should not report a head hit.");
            Debug.Log("SAKURA_AVATAR_TOUCH_CHECKS_OK");
        }

        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
