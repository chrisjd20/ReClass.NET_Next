using System;
using System.Collections.Generic;
using System.Globalization;

namespace ReClassNET.Debugger
{
    // No code compilation: the AST evaluates only captured registers and bounded reads.
    public sealed class WatchCondition
    {
        private struct Value { public ulong Bits; public bool Signed; public Value(ulong bits,bool signed=false){Bits=bits;Signed=signed;} }
        private sealed class Scope
        {
            public IDictionary<string,ulong> Registers;
            public Func<ulong,int,byte[]> Read;
            public ulong Thread,Hits;
            public int Reads;
        }
        private delegate Value Node(Scope scope);
        private readonly Node root;
        private WatchCondition(Node root){this.root=root;}
        public static WatchCondition Parse(string source)
        {
            if(string.IsNullOrWhiteSpace(source)) return new WatchCondition(s=>new Value(1));
            if(source.Length>2048) throw new FormatException("Condition exceeds 2 KiB.");
            var parser=new Parser(source); var root=parser.Expression(0,0);
            if(parser.Token!="") throw new FormatException("Unexpected token: "+parser.Token);
            return new WatchCondition(root);
        }
        public bool Evaluate(IDictionary<string,ulong> registers,ulong thread,ulong hits,Func<ulong,int,byte[]> read)
        {
            return root(new Scope{Registers=registers,Thread=thread,Hits=hits,Read=read}).Bits!=0;
        }
        private static Value Register(Scope s,string name)
        {
            if(name=="threadid") return new Value(s.Thread);
            if(name=="hitcount") return new Value(s.Hits);
            ulong bits;
            if(s.Registers.TryGetValue(name,out bits)) return new Value(bits);
            var flags=new Dictionary<string,int>{{"cf",0},{"pf",2},{"af",4},{"zf",6},{"sf",7},{"tf",8},{"if",9},{"df",10},{"of",11}};
            int bit;
            if(flags.TryGetValue(name,out bit) && s.Registers.TryGetValue("rflags",out bits)) return new Value((bits>>(bit))&1);
            string full=null; int width=0,shift=0;
            var aliases=new Dictionary<string,string>{{"a","rax"},{"b","rbx"},{"c","rcx"},{"d","rdx"},{"si","rsi"},{"di","rdi"},{"sp","rsp"},{"bp","rbp"},{"ip","rip"}};
            foreach(var a in aliases)
            {
                if(name=="e"+a.Key+(a.Key.Length==1?"x":"")){full=a.Value;width=32;break;}
                if(name==a.Key+(a.Key.Length==1?"x":"")){full=a.Value;width=16;break;}
                if(a.Key.Length==1 && (name==a.Key+"l" || name==a.Key+"h")){full=a.Value;width=8;shift=name.EndsWith("h")?8:0;break;}
                if(a.Key.Length==2 && name==a.Key+"l"){full=a.Value;width=8;break;}
            }
            if(full==null && name.StartsWith("r",StringComparison.Ordinal) && name.Length>2)
            {
                char suffix=name[name.Length-1];
                if(suffix=='d'||suffix=='w'||suffix=='b') {full=name.Substring(0,name.Length-1);width=suffix=='d'?32:suffix=='w'?16:8;}
            }
            if(full!=null && s.Registers.TryGetValue(full,out bits)) return new Value((bits>>shift)&((1UL<<width)-1));
            throw new InvalidOperationException("Register unavailable: "+name);
        }
        private sealed class Parser
        {
            private readonly string text; private int position,nodes;
            public string Token {get;private set;}
            public Parser(string text){this.text=text;Next();}
            private void Next()
            {
                while(position<text.Length && char.IsWhiteSpace(text[position])) position++;
                if(position==text.Length){Token="";return;}
                int start=position++; char c=text[start];
                if(char.IsLetterOrDigit(c)||c=='_') {while(position<text.Length&&(char.IsLetterOrDigit(text[position])||text[position]=='_')) position++;}
                else if(position<text.Length)
                {
                    var pair=text.Substring(start,2);
                    if(pair=="&&"||pair=="||"||pair=="=="||pair=="!="||pair=="<="||pair==">="||pair=="<<"||pair==">>") position++;
                }
                Token=text.Substring(start,position-start).ToLowerInvariant();
            }
            private static int Precedence(string op)
            {
                switch(op){case "||":return 1;case "&&":return 2;case "|":return 3;case "^":return 4;case "&":return 5;case "==":case "!=":return 6;case "<":case ">":case "<=":case ">=":return 7;case "<<":case ">>":return 8;case "+":case "-":return 9;case "*":case "/":case "%":return 10;default:return -1;}
            }
            private void Count(int depth){if(++nodes>256||depth>32) throw new FormatException("Condition exceeds complexity limits.");}
            private void Expect(string token){if(Token!=token) throw new FormatException("Expected "+token);Next();}
            public Node Expression(int precedence,int depth)
            {
                Count(depth); Node left; string token=Token; Next();
                if(token=="(" ){left=Expression(0,depth+1);Expect(")");}
                else if(token=="!"||token=="~"||token=="-"||token=="+")
                {
                    var inner=Expression(11,depth+1);
                    left=s=>{var v=inner(s);return new Value(token=="!"?(v.Bits==0?1UL:0UL):token=="~"?~v.Bits:token=="-"?unchecked(0UL-v.Bits):v.Bits,v.Signed);};
                }
                else if(token.Length>0&&char.IsDigit(token[0]))
                {
                    ulong value;
                    bool ok=token.StartsWith("0x")?ulong.TryParse(token.Substring(2),NumberStyles.HexNumber,CultureInfo.InvariantCulture,out value):ulong.TryParse(token,NumberStyles.None,CultureInfo.InvariantCulture,out value);
                    if(!ok) throw new FormatException("Invalid integer: "+token);
                    left=s=>new Value(value);
                }
                else if(token.Length>0&&(char.IsLetter(token[0])||token[0]=='_'))
                {
                    if(Token=="(")
                    {
                        if(token!="mem8"&&token!="mem16"&&token!="mem32"&&token!="mem64"&&token!="signed32"&&token!="signed64") throw new FormatException("Function unavailable: "+token);
                        Next();var arg=Expression(0,depth+1);Expect(")");
                        left=s=>{
                            var v=arg(s);
                            if(token=="signed32") return new Value(unchecked((ulong)(long)(int)v.Bits),true);
                            if(token=="signed64") return new Value(v.Bits,true);
                            if(++s.Reads>16) throw new InvalidOperationException("Condition exceeds 16 memory reads.");
                            int size=int.Parse(token.Substring(3),CultureInfo.InvariantCulture)/8;
                            var bytes=s.Read(v.Bits,size);
                            if(bytes==null||bytes.Length!=size) throw new InvalidOperationException("Condition memory read failed.");
                            ulong result=0; for(int i=0;i<size;i++) result|=(ulong)bytes[i]<<(i*8);
                            return new Value(result);
                        };
                    }
                    else left=s=>Register(s,token);
                }
                else throw new FormatException("Expected an operand.");
                while(Precedence(Token)>=precedence)
                {
                    string op=Token;int p=Precedence(op);Next();var right=Expression(p+1,depth+1);var prior=left;Count(depth);
                    left=s=>{
                        var a=prior(s);
                        if(op=="&&"&&a.Bits==0) return new Value(0);
                        if(op=="||"&&a.Bits!=0) return new Value(1);
                        var b=right(s);bool signed=a.Signed||b.Signed;
                        int cmp=signed?unchecked((long)a.Bits).CompareTo(unchecked((long)b.Bits)):a.Bits.CompareTo(b.Bits);
                        switch(op)
                        {
                            case "||":case "&&":return new Value(b.Bits!=0?1UL:0);
                            case "==":return new Value(a.Bits==b.Bits?1UL:0);case "!=":return new Value(a.Bits!=b.Bits?1UL:0);
                            case "<":return new Value(cmp<0?1UL:0);case ">":return new Value(cmp>0?1UL:0);case "<=":return new Value(cmp<=0?1UL:0);case ">=":return new Value(cmp>=0?1UL:0);
                            case "+":return new Value(unchecked(a.Bits+b.Bits),signed);case "-":return new Value(unchecked(a.Bits-b.Bits),signed);case "*":return new Value(unchecked(a.Bits*b.Bits),signed);
                            case "/":return new Value(a.Bits/b.Bits,signed);case "%":return new Value(a.Bits%b.Bits,signed);
                            case "&":return new Value(a.Bits&b.Bits,signed);case "|":return new Value(a.Bits|b.Bits,signed);case "^":return new Value(a.Bits^b.Bits,signed);
                            case "<<":return new Value(a.Bits<<(int)(b.Bits&63),signed);case ">>":return new Value(signed?unchecked((ulong)((long)a.Bits>>(int)(b.Bits&63))):a.Bits>>(int)(b.Bits&63),signed);
                            default:throw new FormatException("Invalid operator.");
                        }
                    };
                }
                return left;
            }
        }
    }
}
