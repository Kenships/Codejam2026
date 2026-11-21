using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Core.Utility.CustomAttributes.Editor
{
    [CustomPropertyDrawer(typeof(InterfaceReference<>))]
    [CustomPropertyDrawer(typeof(InterfaceReference<,>))]
    public class InterfaceReferenceDrawer : PropertyDrawer
    {
        private const string UnderlyingValueFieldName = "underlyingValue";

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            var underlyingProperty = property.FindPropertyRelative(UnderlyingValueFieldName);
            var args = GetArguments(fieldInfo);
            
            EditorGUI.BeginProperty(position, label, property);
            
            var assignedObject = EditorGUI.ObjectField(position, label, underlyingProperty.objectReferenceValue, typeof(Object), true);

            if (assignedObject != null)
            {
                if (assignedObject is GameObject gameObject)
                {
                    ValidateAndAssignObject(underlyingProperty, gameObject.GetComponent(args.InterfaceType), args.InterfaceType.Name, gameObject.name);
                }
                else
                {
                    ValidateAndAssignObject(underlyingProperty, assignedObject, args.InterfaceType.Name);
                }
            }
            else
            {
                underlyingProperty.objectReferenceValue = null;
            }
            
            EditorGUI.EndProperty();
            InterfaceReferenceUtil.OnGUI(position, underlyingProperty, label, args);
        }

        static InterfaceArgs GetArguments(FieldInfo fieldInfo)
        {
            Type fieldType = fieldInfo.FieldType;

            bool TryGetTypesFromInterfaceReference(Type type, out Type objType, out Type intfType)
            {
                objType = intfType = null;

                if (type?.IsGenericType != true) return false;
                
                var genericType = type.GetGenericTypeDefinition();
                if (genericType == typeof(InterfaceReference<>)) type = type.BaseType;

                if (type?.GetGenericTypeDefinition() == typeof(InterfaceReference<,>))
                {
                    var types =  type.GetGenericArguments();
                    intfType = types[0];
                    objType = types[1];
                    return true;
                }

                return false;
            }

            void GetTypesFromList(Type type, out Type objType, out Type intfType)
            {
                objType = intfType = null;
                
                var listInterfaces = type.GetInterfaces()
                    .FirstOrDefault(x => x.IsGenericType && x.GetGenericTypeDefinition() == typeof(IList<>));

                if (listInterfaces != null)
                {
                    var elementType = listInterfaces.GetGenericArguments()[0];
                    TryGetTypesFromInterfaceReference(elementType, out objType, out intfType);
                }
            }

            if (!TryGetTypesFromInterfaceReference(fieldType, out Type objectType, out Type interfaceType))
            {
                GetTypesFromList(fieldType, out objectType, out objectType);
            }
            
            return new InterfaceArgs(objectType, interfaceType);
        }

        static void ValidateAndAssignObject(SerializedProperty property, Object targetObject,
            string interfaceName, string componentNameOrType = null)
        {
            if (targetObject != null)
            {
                property.objectReferenceValue = targetObject;
            }
            else
            {
                Debug.LogWarning(@$"The {(componentNameOrType != null 
                        ? $"GameObject '{componentNameOrType}'" 
                        : $"assigned object")} does not have a component that implements '{interfaceName}'"
                    );
                property.objectReferenceValue = null;
            }
        }
        
    }

    public struct InterfaceArgs
    {
        public readonly Type ObjectType;
        public readonly Type InterfaceType;

        public InterfaceArgs(Type objectType, Type interfaceType)
        {
            ObjectType = objectType;
            InterfaceType = interfaceType;
        }
    }
}
