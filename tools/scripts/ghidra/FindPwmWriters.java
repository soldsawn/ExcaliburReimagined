// Ghidra headless script: 0x18xx (IT5570 PWM/EC blokları) DPTR yüklemelerini ve içinde bulundukları fonksiyonları dök.
// Çıktı: scriptArgs[0] dosyasına pseudocode.
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.Instruction;
import ghidra.program.model.scalar.Scalar;

import java.io.PrintWriter;
import java.util.LinkedHashMap;
import java.util.Map;
import java.util.TreeSet;

public class FindPwmWriters extends GhidraScript {
    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        long lo = Long.decode(args[1]);
        long hi = Long.decode(args[2]);
        // Ham ikilide giriş noktası yok: 8051 reset + kesme vektörlerini (8 baytlık aralıklarla) giriş say.
        for (int a = 0; a < 0x80; a += (a == 0 ? 3 : 8)) {
            Address addr = toAddr(a);
            currentProgram.getSymbolTable().addExternalEntryPoint(addr);
            disassemble(addr);
        }
        // Bank penceresi (0x8000+): kesin giriş yok, RET/RETI'den hemen sonrasını fonksiyon başı say.
        if (args.length > 3 && args[3].equals("seed")) {
            for (long a = 0x8000; a < 0xFFFF; a++) {
                int b = getByte(toAddr(a)) & 0xFF;
                if (b == 0x22 || b == 0x32) {
                    Address next = toAddr(a + 1);
                    disassemble(next);
                    if (getFunctionAt(next) == null) createFunction(next, null);
                }
            }
        }
        analyzeAll(currentProgram);
        Map<Function, TreeSet<Long>> hits = new LinkedHashMap<>();
        for (Instruction ins : currentProgram.getListing().getInstructions(true)) {
            if (!ins.getMnemonicString().equals("MOV") || ins.getNumOperands() != 2) continue;
            if (!ins.getDefaultOperandRepresentation(0).equals("DPTR")) continue;
            Object[] o = ins.getOpObjects(1);
            if (o.length == 0 || !(o[0] instanceof Scalar)) continue;
            long v = ((Scalar) o[0]).getUnsignedValue();
            if (v < lo || v > hi) continue;
            Function f = currentProgram.getFunctionManager().getFunctionContaining(ins.getAddress());
            if (f == null) { println("fonksiyon yok @ " + ins.getAddress() + " v=" + Long.toHexString(v)); continue; }
            hits.computeIfAbsent(f, k -> new TreeSet<>()).add(v);
        }
        DecompInterface di = new DecompInterface();
        di.openProgram(currentProgram);
        try (PrintWriter w = new PrintWriter(args[0], "UTF-8")) {
            for (Map.Entry<Function, TreeSet<Long>> e : hits.entrySet()) {
                Function f = e.getKey();
                w.println("// ==== " + f.getName() + " @ " + f.getEntryPoint() + " xdata: " + e.getValue().stream().map(Long::toHexString).toList());
                DecompileResults r = di.decompileFunction(f, 60, monitor);
                w.println(r.decompileCompleted() ? r.getDecompiledFunction().getC() : "// decompile başarısız");
            }
        }
        println("fonksiyon sayısı: " + hits.size());
    }
}
