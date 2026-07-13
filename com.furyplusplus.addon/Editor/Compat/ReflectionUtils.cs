using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace FuryPlusPlus {
    /**
     * Shared low-level reflection helpers. All VRCFury member lookups live in Editor/Compat/
     * (here or in per-area compat holders); patch bodies never call GetMethod/GetType directly.
     */
    internal static class ReflectionUtils {
        private static readonly Dictionary<string, Type> TypeCache =
            new Dictionary<string, Type>(StringComparer.Ordinal);

        // The startup lookups cluster in a handful of VRCFury/VRC SDK assemblies, so probe
        // past hits first instead of scanning the whole AppDomain on every miss.
        private static readonly List<Assembly> HitAssemblies = new List<Assembly>();

        /** Throws MissingMemberException with a precise per-member message when a lookup failed. */
        internal static T Demand<T>(T member, string what) where T : class {
            if (member == null) {
                throw new MissingMemberException("VRCFury member not found: " + what);
            }
            return member;
        }

        internal static Type FindType(string fullName) {
            if (TypeCache.TryGetValue(fullName, out var cached)) return cached;

            Type found = null;
            foreach (var assembly in HitAssemblies) {
                found = assembly.GetType(fullName, false);
                if (found != null) break;
            }
            if (found == null) {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) {
                    found = assembly.GetType(fullName, false);
                    if (found != null) break;
                }
            }
            if (found != null && !HitAssemblies.Contains(found.Assembly)) {
                HitAssemblies.Add(found.Assembly);
            }
            TypeCache[fullName] = found;
            return found;
        }

        internal static IEnumerable<MethodInfo> FindDeclaredMethods(string typeName, string methodName) {
            var type = FindType(typeName);
            if (type == null) return Array.Empty<MethodInfo>();
            return type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
                                   BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(method => method.Name == methodName)
                .Where(method => !method.ContainsGenericParameters)
                .Where(method => !method.IsAbstract)
                .Where(method => method.GetMethodBody() != null);
        }

        internal static MethodInfo FindUniqueMethod(
            Type type,
            string name,
            Func<MethodInfo, bool> predicate
        ) {
            return FindUniqueMethod(
                type,
                name,
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
                BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                method => !method.ContainsGenericParameters && predicate(method)
            );
        }

        internal static MethodInfo FindNoArgVoid(Type type, string name) {
            return FindMethodWithSignature(type, name, typeof(void));
        }

        internal static MethodInfo FindMethodWithSignature(
            Type type,
            string name,
            Type returnType,
            params Type[] parameterTypes
        ) {
            return FindUniqueMethod(type, name, method => {
                if (method.ReturnType != returnType) return false;
                var parameters = method.GetParameters();
                if (parameters.Length != parameterTypes.Length) return false;
                for (var i = 0; i < parameters.Length; i++) {
                    if (parameters[i].ParameterType != parameterTypes[i]) return false;
                }
                return true;
            });
        }

        internal static object CreateEmptyImmutableSet(Type setType) {
            try {
                var arguments = setType.GetGenericArguments();
                if (arguments.Length != 1) return null;
                // Unity also loads a private copy inside ReportGeneratorMerged. Resolve
                // from the set type's own assembly so the empty set is assignable to
                // VRCFury's System.Collections.Immutable contract.
                var openType = setType.Assembly.GetType(
                    "System.Collections.Immutable.ImmutableHashSet`1",
                    false
                );
                if (openType == null) return null;
                var closedType = openType.MakeGenericType(arguments[0]);
                var empty = closedType
                    .GetField("Empty", BindingFlags.Static | BindingFlags.Public)?
                    .GetValue(null);
                return empty != null && setType.IsInstanceOfType(empty) ? empty : null;
            } catch {
                return null;
            }
        }

        internal static object InvokeUnwrapped(MethodInfo method, object instance, object[] args) {
            try {
                return method.Invoke(instance, args);
            } catch (TargetInvocationException e) when (e.InnerException != null) {
                ExceptionDispatchInfo.Capture(e.InnerException).Throw();
                throw;
            }
        }

        internal static MethodInfo FindUniqueMethod(
            Type type,
            string name,
            BindingFlags flags,
            Func<MethodInfo, bool> predicate
        ) {
            if (type == null) return null;
            // Ambiguity means the pinned signature no longer identifies one method; treat
            // it as missing so the caller disables its module instead of throwing here.
            MethodInfo match = null;
            foreach (var method in type.GetMethods(flags)) {
                if (method.Name != name || !predicate(method)) continue;
                if (match != null) return null;
                match = method;
            }
            return match;
        }
    }
}
