using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace SWFToPAM;

public class ExtendedBinaryWriter : BinaryWriter
{
    public long Position
    {
        get
        {
            return BaseStream.Position;
        }
        set
        {
            BaseStream.Position = value;
        }
    }

    private Stack<long> m_steps = new(64);

    public ExtendedBinaryWriter(Stream inStream) : base(inStream)
    {
        m_steps = new();
    }

    public void StepIn(long offset)
    {
        m_steps.Push(Position);
        Position = offset;
    }

    public void StepOut()
    {
        Debug.Assert(m_steps.Count > 0);
        Position = m_steps.Pop();
    }

    public void WriteUShortSizedString(string value)
    {
        Write((ushort)value.Length);
        foreach (char c in value)
            Write(c);
    }
}
