"""Embedding retrieval-quality comparison across local runtimes over /v1/embeddings.

12 topics x 4 documents = 48 documents; 3 paraphrased queries per topic = 36 queries.
Queries deliberately avoid reusing the documents' distinctive nouns wherever possible, so a
lexical matcher cannot score well by surface overlap alone. The larger set exists because a
15-document corpus could not separate the providers: one extra relevant hit moved Recall@5 by
0.033, so the entire observed gap there was a single document and therefore not decidable.
"""
import json
import math
import sys
import time
import urllib.request

CORPUS = [
    ("astro_1", "astronomy", "Telescopes gather starlight with a curved mirror, letting observers resolve faint galaxies invisible to the unaided eye."),
    ("astro_2", "astronomy", "Kepler's laws describe an elliptical orbit and how a body accelerates as it approaches its primary."),
    ("astro_3", "astronomy", "Outer-planet probes borrow momentum from Jupiter and Saturn to reach the heliopause without vast propellant."),
    ("astro_4", "astronomy", "A red shift in a distant object's spectrum indicates it is receding, which underpins the expanding-universe model."),
    ("cook_1", "cooking", "A honed blade and a claw grip make reducing an onion to even pieces quicker and less hazardous than a blunt edge."),
    ("cook_2", "cooking", "Gluten strands and carbon dioxide from fermenting yeast are what let a kneaded dough swell in the oven."),
    ("cook_3", "cooking", "Deglazing caramelised residue from a hot pan with wine, then mounting it with chilled butter, yields a glossy sauce."),
    ("cook_4", "cooking", "Resting a roast after cooking lets the juices redistribute so they do not run out when it is carved."),
    ("net_1", "networking", "A hostname is translated to a numeric address by walking a hierarchy of nameservers from the root downward."),
    ("net_2", "networking", "A forwarding device consults its table and picks the next hop using the destination field of each datagram."),
    ("net_3", "networking", "A three-way handshake opens a reliable stream, and unacknowledged segments are sent again after a timeout."),
    ("net_4", "networking", "Network address translation lets many private hosts share a single public address by rewriting port mappings."),
    ("garden_1", "gardening", "Working decayed organic matter into heavy ground opens drainage, and into light ground improves moisture holding."),
    ("garden_2", "gardening", "Cutting back orchard stock while it is leafless in late winter promotes vigorous shoots and an open canopy."),
    ("garden_3", "gardening", "A heap needs both sappy green material and dry brown material, turned to keep the process oxygenated."),
    ("garden_4", "gardening", "Mulching around the base suppresses competing weeds and slows evaporation from the root zone in summer."),
    ("fin_1", "finance", "Assigning every unit of income to a purpose until nothing is unallocated is the core of the envelope approach."),
    ("fin_2", "finance", "When the central bank lifts its policy rate, floating repayments rise and commercial borrowing becomes dearer."),
    ("fin_3", "finance", "Sheltered retirement accounts reduce present taxable earnings while returns compound untaxed until drawdown."),
    ("fin_4", "finance", "Spreading holdings across uncorrelated assets reduces the damage any single position can do to a portfolio."),
    ("med_1", "medicine", "Antibiotics act on bacterial targets and confer no benefit against viral illness, so misuse breeds resistance."),
    ("med_2", "medicine", "Vaccines present a harmless fragment so the immune system can build memory before a real encounter."),
    ("med_3", "medicine", "Blood pressure is recorded as two figures, the pressure during contraction and the pressure between beats."),
    ("med_4", "medicine", "Rehydration with balanced salts and sugar replaces what is lost during a bout of gastrointestinal illness."),
    ("law_1", "law", "A contract generally requires an offer, its acceptance, and something of value exchanged between the parties."),
    ("law_2", "law", "The burden of proof in a criminal matter rests with the prosecution and must overcome reasonable doubt."),
    ("law_3", "law", "Copyright protects a fixed expression rather than the underlying idea, which anyone else remains free to use."),
    ("law_4", "law", "A limitation period bars a claim once a set interval has run from when the cause of action arose."),
    ("geo_1", "geology", "Slow convection beneath the crust drives rigid plates apart at ridges and together at subduction margins."),
    ("geo_2", "geology", "Sediment compacted over long spans lithifies into layered rock that preserves the order of deposition."),
    ("geo_3", "geology", "Radiometric dating uses the steady decay of unstable isotopes to assign an absolute age to a sample."),
    ("geo_4", "geology", "Groundwater dissolving carbonate bedrock hollows out caverns and produces sinkholes at the surface."),
    ("music_1", "music", "Equal temperament divides the octave into twelve steps so a keyboard can modulate to any key acceptably."),
    ("music_2", "music", "A note's perceived pitch follows the frequency of vibration, while its timbre comes from the overtone mix."),
    ("music_3", "music", "Counterpoint sets independent melodic lines against each other so each remains singable on its own."),
    ("music_4", "music", "A time signature states how many beats fill a measure and which value is counted as one beat."),
    ("chem_1", "chemistry", "A catalyst opens a lower-energy route for a reaction and emerges from it chemically unchanged."),
    ("chem_2", "chemistry", "The periodic arrangement groups elements whose outer electron structure gives them similar behaviour."),
    ("chem_3", "chemistry", "An acid donates protons and a base accepts them, and their combination yields a salt plus water."),
    ("chem_4", "chemistry", "Distillation separates a mixture by exploiting the different temperatures at which components vaporise."),
    ("sport_1", "sports", "Endurance improves when training alternates hard efforts with genuine recovery rather than constant strain."),
    ("sport_2", "sports", "Offside judgements turn on the attacker's position relative to defenders at the instant the ball is played."),
    ("sport_3", "sports", "A marathon runner paces to avoid depleting stored carbohydrate before the closing stage of the distance."),
    ("sport_4", "sports", "Strength work adapts tissue by progressively increasing load rather than by repeating an unchanging routine."),
    ("arch_1", "architecture", "An arch redirects load outward and downward, letting masonry span an opening it could not otherwise cross."),
    ("arch_2", "architecture", "Cantilevered floors project past their support, which places the upper fibres of the beam in tension."),
    ("arch_3", "architecture", "Passive design orients glazing and mass so a building is tempered by the sun rather than by machinery."),
    ("arch_4", "architecture", "A curtain wall carries only its own weight, with the frame behind it taking the building's structural load."),
]

QUERIES = [
    ("how do observers make out extremely dim star systems at great distance", "astronomy"),
    ("why do spacecraft swing by giant planets instead of carrying more fuel", "astronomy"),
    ("what does a shift toward longer wavelengths tell us about a far object", "astronomy"),
    ("what knife habit lowers the chance of injury when preparing vegetables", "cooking"),
    ("what causes a kneaded loaf to expand as it bakes", "cooking"),
    ("how do you turn browned residue in a skillet into a smooth sauce", "cooking"),
    ("how is a readable site name converted into numbers machines route on", "networking"),
    ("how does a box choose where to pass a packet along next", "networking"),
    ("how is a dependable connection opened and lost pieces recovered", "networking"),
    ("how can I make dense waterlogged soil drain better before planting", "gardening"),
    ("when should dormant fruit stock be trimmed for vigorous regrowth", "gardening"),
    ("what balance does a decomposition heap need to stay aerobic", "gardening"),
    ("a budgeting method where no income is left without a job", "finance"),
    ("what happens to floating loan repayments when policy rates climb", "finance"),
    ("how does a sheltered account change tax now versus later", "finance"),
    ("why do these drugs do nothing for a viral infection", "medicine"),
    ("how does a preparation train immunity before real exposure", "medicine"),
    ("what do the two numbers in a blood pressure reading mean", "medicine"),
    ("what three things are usually needed to form a binding agreement", "law"),
    ("who must prove the case in a criminal trial and to what standard", "law"),
    ("does protection cover the concept itself or only the fixed form", "law"),
    ("what moves the rigid outer shell of the planet around", "geology"),
    ("how is an absolute age assigned to a rock sample", "geology"),
    ("how do underground voids and surface collapses form in limestone", "geology"),
    ("why can a keyboard play in any key without retuning", "music"),
    ("what makes two instruments sound different on the same note", "music"),
    ("how many beats are in a bar and which note gets the beat", "music"),
    ("what lets a reaction proceed faster without being consumed", "chemistry"),
    ("why do elements in the same column behave alike", "chemistry"),
    ("how is a liquid mixture split using boiling points", "chemistry"),
    ("how should training be structured to build stamina safely", "sports"),
    ("how is an attacking player's position judged when the ball is played", "sports"),
    ("how does a distance runner avoid running out of fuel late", "sports"),
    ("what lets stonework span a gap it otherwise could not", "architecture"),
    ("which part of a projecting beam is pulled apart", "architecture"),
    ("how can a building stay comfortable without mechanical systems", "architecture"),
]


def embed(url, headers, model, text):
    payload = {"input": text}
    if model:
        payload["model"] = model
    request = urllib.request.Request(
        url, data=json.dumps(payload).encode(),
        headers={**headers, "Content-Type": "application/json"})
    with urllib.request.urlopen(request, timeout=300) as response:
        return json.loads(response.read())["data"][0]["embedding"]


def cosine(a, b):
    n = min(len(a), len(b))
    dot = sum(a[i] * b[i] for i in range(n))
    na = math.sqrt(sum(x * x for x in a))
    nb = math.sqrt(sum(x * x for x in b))
    return dot / (na * nb) if na and nb else 0.0


def evaluate(name, url, headers, model, k=5):
    vectors = {doc_id: embed(url, headers, model, text) for doc_id, _, text in CORPUS}
    dims = len(next(iter(vectors.values())))

    recall = mrr = ndcg = 0.0
    latencies = []
    per_topic_miss = {}

    for query, topic in QUERIES:
        started = time.perf_counter()
        qv = embed(url, headers, model, query)
        latencies.append((time.perf_counter() - started) * 1000)

        ranked = sorted(
            ((cosine(qv, vectors[d]), d, t) for d, t, _ in CORPUS), reverse=True)[:k]
        relevant = sum(1 for _, t, _ in CORPUS if t == topic)
        hit_positions = [i for i, (_, _, t) in enumerate(ranked) if t == topic]

        recall += len(hit_positions) / relevant
        if hit_positions:
            mrr += 1.0 / (hit_positions[0] + 1)
        else:
            per_topic_miss[topic] = per_topic_miss.get(topic, 0) + 1
        dcg = sum(1.0 / math.log2(i + 2) for i in hit_positions)
        idcg = sum(1.0 / math.log2(i + 2) for i in range(min(k, relevant)))
        ndcg += dcg / idcg if idcg else 0.0

    n = len(QUERIES)
    latencies.sort()
    return {
        "provider": name, "model": model, "dims": dims,
        "recall": recall / n, "mrr": mrr / n, "ndcg": ndcg / n,
        "median_ms": latencies[len(latencies) // 2],
        "p95_ms": latencies[min(len(latencies) - 1, int(len(latencies) * 0.95))],
        "topics_fully_missed": per_topic_miss,
    }


if __name__ == "__main__":
    token = sys.argv[1]
    targets = [
        ("PaRuX", "http://127.0.0.1:51123/v1/embeddings", {"X-PaRuX-Token": token}, "parux-embedding"),
        ("LM Studio", "http://127.0.0.1:1234/v1/embeddings", {}, "text-embedding-nomic-embed-text-v1.5"),
    ]
    results = []
    for name, url, headers, model in targets:
        try:
            results.append(evaluate(name, url, headers, model))
        except Exception as exc:  # a provider that cannot serve is recorded, never scored as zero
            print(f"{name}: unavailable ({exc})")

    print(f"\n{len(CORPUS)} documents / {len(QUERIES)} queries / {len(set(t for _, t, _ in CORPUS))} topics, top-5\n")
    print(f"{'provider':10s} {'dims':>5s} {'Recall@5':>9s} {'MRR':>6s} {'nDCG@5':>7s} {'median':>9s} {'p95':>9s}")
    for r in results:
        print(f"{r['provider']:10s} {r['dims']:5d} {r['recall']:9.3f} {r['mrr']:6.3f} "
              f"{r['ndcg']:7.3f} {r['median_ms']:8.1f}ms {r['p95_ms']:8.1f}ms")
    for r in results:
        if r["topics_fully_missed"]:
            print(f"  {r['provider']} returned no relevant doc for: {r['topics_fully_missed']}")
