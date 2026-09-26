// Kiota's JSON writer turns a null enum into the string "null" (writeEnumValue formats every value), which the API
// rejects. This writer keeps null as null, so optional enums can be cleared like every other property.
import type { SerializationWriter, SerializationWriterFactory, SerializationWriterFactoryRegistry } from '@microsoft/kiota-abstractions';
import { JsonSerializationWriter, JsonSerializationWriterFactory } from '@microsoft/kiota-serialization-json';

class NullSafeJsonSerializationWriter extends JsonSerializationWriter {
  constructor() {
    super();
    const writeEnumValue = this.writeEnumValue;
    this.writeEnumValue = (key, ...values) => {
      if (values.length === 1 && values[0] === null) {
        this.writeNullValue(key);
        return;
      }

      writeEnumValue(key, ...values);
    };
  }
}

class NullSafeJsonSerializationWriterFactory extends JsonSerializationWriterFactory implements SerializationWriterFactory {
  override getSerializationWriter(contentType: string): SerializationWriter {
    super.getSerializationWriter(contentType); // validates the content type
    return new NullSafeJsonSerializationWriter();
  }
}

/** Replaces the JSON writer of a client's registry (after the generated client registered its defaults). */
export function useNullSafeJson(registry: SerializationWriterFactoryRegistry): void {
  registry.contentTypeAssociatedFactories.set('application/json', new NullSafeJsonSerializationWriterFactory());
}
