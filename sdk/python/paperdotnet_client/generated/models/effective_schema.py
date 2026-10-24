from __future__ import annotations
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union

if TYPE_CHECKING:
    from .schema_content_type import SchemaContentType
    from .schema_field import SchemaField
    from .schema_rule import SchemaRule

@dataclass
class EffectiveSchema(AdditionalDataHolder, Parsable):
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The content_types property
    content_types: Optional[list[SchemaContentType]] = None
    # The fields property
    fields: Optional[list[SchemaField]] = None
    # The rules property
    rules: Optional[list[SchemaRule]] = None

    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> EffectiveSchema:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: EffectiveSchema
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return EffectiveSchema()

    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        from .schema_content_type import SchemaContentType
        from .schema_field import SchemaField
        from .schema_rule import SchemaRule

        from .schema_content_type import SchemaContentType
        from .schema_field import SchemaField
        from .schema_rule import SchemaRule

        fields: dict[str, Callable[[Any], None]] = {
            "content_types": lambda n : setattr(self, 'content_types', n.get_collection_of_object_values(SchemaContentType)),
            "fields": lambda n : setattr(self, 'fields', n.get_collection_of_object_values(SchemaField)),
            "rules": lambda n : setattr(self, 'rules', n.get_collection_of_object_values(SchemaRule)),
        }
        return fields

    def serialize(self,writer: SerializationWriter) -> None:
        """
        Serializes information the current object
        param writer: Serialization writer to use to serialize this model
        Returns: None
        """
        if writer is None:
            raise TypeError("writer cannot be null.")
        writer.write_collection_of_object_values("content_types", self.content_types)
        writer.write_collection_of_object_values("fields", self.fields)
        writer.write_collection_of_object_values("rules", self.rules)
        writer.write_additional_data_value(self.additional_data)


