using AIChat.Utils;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace AIChat.Unity
{
    public static class GameBridge
    {
        public static MonoBehaviour _heroineService;
        public static Animator _cachedAnimator;

        public static MethodInfo _changeAnimSmoothMethod;
        public static MethodInfo _lookInitMethod;
        public static MethodInfo _lookAtMethod;

        private static readonly HashSet<string> _loggedWarnings = new HashSet<string>();
        private static readonly BindingFlags InstanceMethodFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        public static bool IsConnected => IsHeroineServiceReady();

        public static bool FindHeroineService()
        {
            if (IsHeroineServiceReady()) return true;

            try
            {
                var allComponents = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>();
                foreach (var comp in allComponents)
                {
                    if (comp == null) continue;

                    Type compType = comp.GetType();
                    if (compType.FullName != "Bulbul.HeroineService") continue;

                    CacheHeroineService(comp, compType);
                    return IsHeroineServiceReady();
                }
            }
            catch (Exception ex)
            {
                LogWarningOnce("FindHeroineService", "查找 HeroineService 失败: " + ex.Message);
            }

            return false;
        }

        public static bool CallNativeChangeAnim(int id)
        {
            return InvokeHeroineMethod(_changeAnimSmoothMethod, new object[] { id }, "ChangeHeroineAnimationForInteger");
        }

        public static bool ControlLookAt(float scale, float speed)
        {
            return InvokeHeroineMethod(_lookAtMethod, new object[] { scale, speed, 0 }, "ChangeLookScaleAnimation");
        }

        public static bool RestoreLookAt()
        {
            return InvokeHeroineMethod(_lookInitMethod, null, "LookInitSlowly");
        }

        private static void CacheHeroineService(MonoBehaviour service, Type serviceType)
        {
            _heroineService = service;
            _cachedAnimator = service.GetComponent<Animator>();

            _changeAnimSmoothMethod = serviceType.GetMethod("ChangeHeroineAnimationForInteger", InstanceMethodFlags);
            _lookInitMethod = serviceType.GetMethod("LookInitSlowly", InstanceMethodFlags);
            _lookAtMethod = serviceType.GetMethod("ChangeLookScaleAnimation", InstanceMethodFlags);

            LogMissingMethodOnce(_changeAnimSmoothMethod, "ChangeHeroineAnimationForInteger");
            LogMissingMethodOnce(_lookInitMethod, "LookInitSlowly");
            LogMissingMethodOnce(_lookAtMethod, "ChangeLookScaleAnimation");

            string objectName = service.gameObject != null ? service.gameObject.name : serviceType.Name;
            Log.Info($"HeroineService 连接成功: {objectName}");
        }

        private static bool IsHeroineServiceReady()
        {
            if (_heroineService != null) return true;

            _cachedAnimator = null;
            _changeAnimSmoothMethod = null;
            _lookInitMethod = null;
            _lookAtMethod = null;
            return false;
        }

        private static bool InvokeHeroineMethod(MethodInfo method, object[] args, string methodName)
        {
            if (!IsHeroineServiceReady() && !FindHeroineService())
            {
                LogWarningOnce("HeroineServiceUnavailable", "HeroineService 尚未连接，无法调用游戏动作。");
                return false;
            }

            if (method == null)
            {
                LogWarningOnce("MissingMethod:" + methodName, $"HeroineService 缺少方法: {methodName}");
                return false;
            }

            try
            {
                method.Invoke(_heroineService, args);
                return true;
            }
            catch (TargetInvocationException ex)
            {
                string message = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
                LogWarningOnce("InvokeFailed:" + methodName, $"{methodName} 调用失败: {message}");
            }
            catch (Exception ex)
            {
                LogWarningOnce("InvokeFailed:" + methodName, $"{methodName} 调用失败: {ex.Message}");
            }

            return false;
        }

        private static void LogMissingMethodOnce(MethodInfo method, string methodName)
        {
            if (method == null)
            {
                LogWarningOnce("MissingMethod:" + methodName, $"HeroineService 缺少方法: {methodName}");
            }
        }

        private static void LogWarningOnce(string key, string message)
        {
            if (_loggedWarnings.Contains(key)) return;
            _loggedWarnings.Add(key);
            Log.Warning(message);
        }
    }
}
