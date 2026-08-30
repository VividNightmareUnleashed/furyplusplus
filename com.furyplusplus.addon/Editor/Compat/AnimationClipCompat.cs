using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace FuryPlusPlus {
    /** Reflected Unity animation-clip settings, kept out of content-key consumers. */
    internal static class AnimationClipCompat {
        private static FieldInfo[] settingsFields;
        private static PropertyInfo[] settingsProperties;

        internal static IEnumerable<(string Name, object Value)> SettingsOf(AnimationClip clip) {
            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            if (settingsFields == null) {
                var type = settings.GetType();
                settingsFields = type.GetFields(BindingFlags.Instance | BindingFlags.Public)
                    .OrderBy(field => field.Name, StringComparer.Ordinal)
                    .ToArray();
                settingsProperties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                    .Where(property => property.CanRead && property.GetIndexParameters().Length == 0)
                    .OrderBy(property => property.Name, StringComparer.Ordinal)
                    .ToArray();
            }
            foreach (var field in settingsFields) {
                yield return (field.Name, field.GetValue(settings));
            }
            foreach (var property in settingsProperties) {
                yield return (property.Name, property.GetValue(settings, null));
            }
        }
    }
}
