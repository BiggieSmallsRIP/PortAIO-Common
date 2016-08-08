﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BinarySerialization.Graph.TypeGraph;

namespace BinarySerialization.Graph.ValueGraph
{
    internal abstract class ValueNode : Node
    {
        private const char PathSeparator = '.';

        protected ValueNode(Node parent, string name, TypeNode typeNode) : base(parent)
        {
            Name = name;
            TypeNode = typeNode;
            Children = new List<ValueNode>();
            Bindings = new List<Func<object>>();
        }

        public TypeNode TypeNode { get; set; }

        public List<ValueNode> Children { get; set; }

        public List<Func<object>> Bindings { get; set; }

        public abstract object Value { get; set; }

        public virtual object BoundValue => Value;

        public virtual bool Visited { get; set; }

        public virtual void Bind()
        {
            var typeNode = TypeNode;

            typeNode.FieldLengthBindings?.Bind(this, () => MeasureOverride());
            typeNode.ItemLengthBindings?.Bind(this, MeasureItemsOverride);
            typeNode.FieldCountBindings?.Bind(this, () => CountOverride());

            if (typeNode.SubtypeBinding != null && typeNode.SubtypeBinding.BindingMode == BindingMode.TwoWay)
            {
                typeNode.SubtypeBinding.Bind(this, () =>
                {
                    Type valueType = GetValueTypeOverride();
                    if (valueType == null)
                        throw new InvalidOperationException("Binding targets must not be null.");

                    var objectTypeNode = (ObjectTypeNode)typeNode;

                    return objectTypeNode.SubTypeKeys[valueType];
                });
            }

            if (typeNode.ItemSerializeUntilBinding != null && typeNode.ItemSerializeUntilBinding.BindingMode == BindingMode.TwoWay)
            {
                typeNode.ItemSerializeUntilBinding.Bind(this, GetLastItemValueOverride);
            }

            if (typeNode.FieldValueAttribute != null)
            {
                typeNode.FieldValueBinding.Bind(this, () => typeNode.FieldValueAttribute.ComputeFinalInternal());
            }

            foreach (ValueNode child in Children)
                child.Bind();
        }

        protected IEnumerable<ValueNode> GetSerializableChildren()
        {
            return Children.Where(child => !child.TypeNode.IsIgnored);
        }

        public void Serialize(BoundedStream stream, EventShuttle eventShuttle, bool align = true)
        {
            try
            {
                var serializeWhenBindings = TypeNode.SerializeWhenBindings;

                if (serializeWhenBindings != null &&
                    !serializeWhenBindings.Any(binding => binding.IsSatisfiedBy(binding.GetBoundValue(this))))
                    return;

                if (align)
                {
                    long? leftAlignment = GetFieldAlignment();
                    if (leftAlignment != null)
                    {
                        Align(stream, leftAlignment, true);
                    }
                }

                long? maxLength = GetConstFieldLength();

                var offset = GetFieldOffset();

                if (offset != null)
                {
                    using (new StreamResetter(stream))
                    {
                        stream.Position = offset.Value;
                        SerializeInternal(stream, eventShuttle, maxLength);
                    }
                }
                else
                {
                    SerializeInternal(stream, eventShuttle, maxLength);
                }

                if (align)
                {
                    long? rightAlignment = GetFieldAlignment();
                    if (rightAlignment != null)
                    {
                        Align(stream, rightAlignment, true);
                    }
                }
            }
            catch (IOException)
            {
                throw;
            }
            catch (Exception e)
            {
                string reference = Name == null
                    ? $"type '{TypeNode.Type}'"
                    : $"member '{Name}'";
                string message = $"Error serializing {reference}.  See inner exception for detail.";
                throw new InvalidOperationException(message, e);
            }
            finally
            {
                Visited = true;
            }
        }

        private void SerializeInternal(BoundedStream stream, EventShuttle eventShuttle, long? maxLength)
        {
            if (maxLength != null)
                stream = new BoundedStream(stream, maxLength.Value);

            // if I need to store serialized data for later...
            if (TypeNode.FieldValueAttribute != null)
            {
                var context = CreateLazySerializationContext();
                TypeNode.FieldValueAttribute.ResetInternal(context);
                var tap = new FieldValueAdapterStream(TypeNode.FieldValueAttribute);
                stream = new TapStream(stream, tap);
            } 

            SerializeOverride(stream, eventShuttle);

            if(TypeNode.FieldValueAttribute != null)
                stream.Flush();
        }

        // this is internal only because of the weird custom subtype case.  If I can figure out a better
        // way to handle that case, this can be protected.
        internal abstract void SerializeOverride(BoundedStream stream, EventShuttle eventShuttle);

        public void Deserialize(BoundedStream stream, EventShuttle eventShuttle)
        {
            try
            {
                var serializeWhenBindings = TypeNode.SerializeWhenBindings;
                if (serializeWhenBindings != null &&
                    !serializeWhenBindings.Any(binding => binding.IsSatisfiedBy(binding.GetValue(this))))
                    return;
                
                long? leftAlignment = GetFieldAlignment();
                if (leftAlignment != null)
                {
                    Align(stream, leftAlignment);
                }

                long? maxLength = GetFieldLength();

                var offset = GetFieldOffset();

                if (offset != null)
                {
                    using (new StreamResetter(stream))
                    {
                        stream.Position = offset.Value;
                        DeserializeInternal(stream, maxLength, eventShuttle);
                    }
                }
                else
                {
                    DeserializeInternal(stream, maxLength, eventShuttle);
                }

                long? rightAlignment = GetFieldAlignment();
                if (rightAlignment != null)
                {
                    Align(stream, rightAlignment);
                }
            }
            catch (IOException)
            {
                throw;
            }
            catch (Exception e)
            {
                string reference = Name == null
                    ? $"type '{TypeNode.Type}'"
                    : $"member '{Name}'";
                string message = $"Error deserializing '{reference}'.  See inner exception for detail.";
                throw new InvalidOperationException(message, e);
            }
            finally
            {
                Visited = true;
            }
        }

        private void Align(BoundedStream stream, long? alignment, bool pad = false)
        {
            if (alignment == null)
                throw new ArgumentNullException(nameof(alignment));

            var position = stream.RelativePosition;
            var delta = (alignment.Value - position % alignment.Value)%alignment.Value;

            if (delta == 0)
                return;

            if (pad)
            {
                var padding = new byte[delta];
                stream.Write(padding, 0, padding.Length);
            }
            else
            {
                for (int i = 0; i < delta; i++)
                {
                    if (stream.ReadByte() < 0)
                        break;
                }
            }

        }

        private void DeserializeInternal(BoundedStream stream, long? maxLength, EventShuttle eventShuttle)
        {
            if (maxLength != null)
                stream = new BoundedStream(stream, maxLength.Value);

            DeserializeOverride(stream, eventShuttle);
        }

        internal abstract void DeserializeOverride(BoundedStream stream, EventShuttle eventShuttle);

        protected long? GetFieldLength()
        {
            var length = GetNumericValue(TypeNode.FieldLengthBindings);
            if (length != null)
                return length;

            var parent = (ValueNode)Parent;
            if (parent?.TypeNode.ItemLengthBindings != null)
            {
                var parentItemLength = parent.TypeNode.ItemLengthBindings.GetValue(parent);
                if(parentItemLength.GetType().IsPrimitive)
                    return Convert.ToInt64(parentItemLength);
            }

            return null;
        }

        protected long? GetConstFieldLength()
        {
            return GetConstNumericValue(TypeNode.FieldLengthBindings) ??
                   (Parent as ValueNode)?.GetConstFieldItemLength();
        }

        protected long? GetFieldAlignment()
        {
            // Field alignment cannot be determined from graph
            // so always go to a const or bound value
            var value = TypeNode.FieldAlignmentBindings?.GetBoundValue(this);
            if (value == null)
                return null;

            return Convert.ToInt64(value);
        }

        protected long? GetFieldCount()
        {
            return GetNumericValue(TypeNode.FieldCountBindings);
        }

        protected long? GetConstFieldCount()
        {
            return GetConstNumericValue(TypeNode.FieldCountBindings);
        }

        protected long? GetFieldItemLength()
        {
            return GetNumericValue(TypeNode.ItemLengthBindings);
        }

        protected long? GetConstFieldItemLength()
        {
            return GetConstNumericValue(TypeNode.ItemLengthBindings);
        }

        protected long? GetFieldOffset()
        {
            return GetNumericValue(TypeNode.FieldOffsetBindings);
        }

        protected long? GetConstFieldOffset()
        {
            return GetConstNumericValue(TypeNode.FieldOffsetBindings);
        }

        protected virtual Endianness GetFieldEndianness()
        {
            Endianness endianness = TypeNode.Endianness ?? Endianness.Inherit;

            if (endianness == Endianness.Inherit)
            {
                var value = TypeNode.FieldEndiannessBindings?.GetBoundValue(this);

                if (value != null)
                {
                    if (value is Endianness)
                    {
                        endianness = (Endianness) Enum.ToObject(typeof (Endianness), value);
                    }
                    else
                    {
                        throw new InvalidOperationException("FieldEndianness converters must return a valid Endianness.");
                    }
                }
            }

            if (endianness == Endianness.Inherit && Parent != null)
            {
                var parent = (ValueNode) Parent;
                endianness = parent.GetFieldEndianness();
            }

            return endianness;
        }

        protected virtual Encoding GetFieldEncoding()
        {
            Encoding encoding = TypeNode.Encoding;

            if (encoding == null)
            {
                var value = TypeNode.FieldEncodingBindings?.GetBoundValue(this);

                if (value != null)
                {
                    if (value is Encoding)
                    {
                        encoding = value as Encoding;
                    }
                    else
                    {
                        throw new InvalidOperationException("FieldEncoding converters must return a valid Encoding.");
                    }
                }
            }

            if (encoding == null && Parent != null)
            {
                var parent = (ValueNode)Parent;
                encoding = parent.GetFieldEncoding();
            }

            return encoding;
        }
        
        private long? GetNumericValue(IBinding binding)
        {
            var value = binding?.GetValue(this);
            if (value == null)
                return null;

            return Convert.ToInt64(value);
        }

        private long? GetConstNumericValue(IBinding binding)
        {
            if (binding == null)
                return null;

            if (!binding.IsConst)
                return null;

            return Convert.ToInt64(binding.ConstValue);
        }

        public ValueNode GetChild(string path)
        {
            string[] memberNames = path.Split(PathSeparator);

            if (memberNames.Length == 0)
                throw new BindingException("Path cannot be empty.");

            ValueNode child = this;
            foreach (string name in memberNames)
            {
                child = child.Children.SingleOrDefault(c => c.Name == name);

                if (child == null)
                    throw new BindingException($"No field found at '{path}'.");
            }

            return child;
        }

        private BinarySerializationContext CreateSerializationContext()
        {
            var parent = Parent as ValueNode;
            return new BinarySerializationContext(Value, parent?.Value, parent?.TypeNode.Type, parent?.CreateSerializationContext());
        }

        public LazyBinarySerializationContext CreateLazySerializationContext()
        {
            var lazyContext = new Lazy<BinarySerializationContext>(CreateSerializationContext);
            return new LazyBinarySerializationContext(lazyContext);
        }

        protected virtual long MeasureOverride()
        {
            var nullStream = new NullStream();
            var boundedStream = new BoundedStream(nullStream);
            Serialize(boundedStream, null, false);
            return boundedStream.RelativePosition;
        }

        protected virtual IEnumerable<long> MeasureItemsOverride()
        {
            throw new InvalidOperationException("Not a collection field.");
        }

        protected virtual long CountOverride()
        {
            throw new InvalidOperationException("Not a collection field.");
        }

        protected virtual Type GetValueTypeOverride()
        {
            throw new InvalidOperationException("Can't set subtypes on this field.");
        }

        protected virtual object GetLastItemValueOverride()
        {
            throw new InvalidOperationException("Not a collection field.");
        }

        protected static bool EndOfStream(BoundedStream stream)
        {
            if (stream.IsAtLimit)
                return true;

            return stream.CanSeek && stream.Position >= stream.Length;
        }
    }
}