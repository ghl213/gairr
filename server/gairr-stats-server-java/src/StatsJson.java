import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/**
 * StatsJson —— StatsStore 专用的最小 JSON 解析器（包私有，零第三方依赖）。
 *
 * 值模型：object→LinkedHashMap&lt;String,Object&gt;、array→ArrayList&lt;Object&gt;、
 * string、number→Double、Boolean、null。支持 \\uXXXX 转义（含代理对原样保留）。
 * 任何非法输入抛 IllegalArgumentException（消息含位置），由 StatsStore.load 统一容错。
 *
 * 注：仅为 data/stats.json 的加载服务，不校验语义；若后续引入统一 MiniJson
 * 工具类（README TODO#1），可整体替换 StatsStore.load 中的调用点。
 */
final class StatsJson {

    private StatsJson() {}

    /** 解析整段文本，返回根值；尾部多余内容视为非法。 */
    static Object parse(String s) {
        P p = new P(s);
        Object v = p.value();
        p.ws();
        if (!p.end()) throw p.err("trailing content");
        return v;
    }

    /** 单遍递归下降解析器。 */
    private static final class P {
        private final String s;
        private int i;

        P(String s) { this.s = s; }

        boolean end() { return i == s.length(); }

        Object value() {
            ws();
            if (i >= s.length()) throw err("eof");
            switch (s.charAt(i)) {
                case '{': return obj();
                case '[': return arr();
                case '"': return str();
                case 't': expect("true");  return Boolean.TRUE;
                case 'f': expect("false"); return Boolean.FALSE;
                case 'n': expect("null");  return null;
                default:  return num();
            }
        }

        private Map<String, Object> obj() {
            i++; // 消费 '{'
            LinkedHashMap<String, Object> m = new LinkedHashMap<>();
            ws();
            if (eat('}')) return m;
            while (true) {
                ws();
                String k = str();
                ws();
                if (!eat(':')) throw err("':'");
                m.put(k, value());
                ws();
                if (eat(',')) continue;
                if (eat('}')) return m;
                throw err("',' or '}'");
            }
        }

        private List<Object> arr() {
            i++; // 消费 '['
            List<Object> list = new ArrayList<>();
            ws();
            if (eat(']')) return list;
            while (true) {
                list.add(value());
                ws();
                if (eat(',')) continue;
                if (eat(']')) return list;
                throw err("',' or ']'");
            }
        }

        private String str() {
            if (!eat('"')) throw err("string");
            StringBuilder sb = new StringBuilder();
            while (i < s.length()) {
                char c = s.charAt(i++);
                if (c == '"') return sb.toString();
                if (c != '\\') { sb.append(c); continue; }
                if (i >= s.length()) break;
                char e = s.charAt(i++);
                switch (e) {
                    case '"': sb.append('"'); break;
                    case '\\': sb.append('\\'); break;
                    case '/': sb.append('/'); break;
                    case 'b': sb.append('\b'); break;
                    case 'f': sb.append('\f'); break;
                    case 'n': sb.append('\n'); break;
                    case 'r': sb.append('\r'); break;
                    case 't': sb.append('\t'); break;
                    case 'u':
                        sb.append((char) Integer.parseInt(s.substring(i, i + 4), 16));
                        i += 4;
                        break;
                    default: throw err("escape");
                }
            }
            throw err("unterminated string");
        }

        private Number num() {
            int st = i;
            while (i < s.length() && "+-0123456789.eE".indexOf(s.charAt(i)) >= 0) i++;
            if (st == i) throw err("number");
            return Double.valueOf(s.substring(st, i));
        }

        void ws() { while (i < s.length() && " \t\r\n".indexOf(s.charAt(i)) >= 0) i++; }

        private boolean eat(char c) {
            if (i < s.length() && s.charAt(i) == c) { i++; return true; }
            return false;
        }

        private void expect(String lit) {
            if (!s.startsWith(lit, i)) throw err("'" + lit + "'");
            i += lit.length();
        }

        IllegalArgumentException err(String what) {
            return new IllegalArgumentException("bad json (" + what + ") at " + i);
        }
    }
}
